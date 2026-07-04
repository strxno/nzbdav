using System.Collections.Concurrent;
using System.Diagnostics;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

public sealed class FileAccessSession : IDisposable
{
    private readonly string _fileName;
    private readonly long _fileSize;
    private readonly long _startOffset;
    private readonly int _segmentCount;
    private readonly int _articleBufferSize;
    private readonly int _configuredMax;
    private readonly string? _rangeHeader;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, ProviderSessionStats> _providerStats = new();
    private readonly object _progressLock = new();

    private long _bytesDelivered;
    private long _segmentsCompleted;
    private long _readAttempts;
    private int _firstByteLogged;
    private long _lastProgressLogMs;
    private bool _completed;

    public FileAccessSession(
        string fileName,
        long fileSize,
        long startOffset,
        int segmentCount,
        int articleBufferSize,
        string? rangeHeader,
        int configuredMax = 0)
    {
        _fileName = fileName;
        _fileSize = fileSize;
        _startOffset = startOffset;
        _segmentCount = segmentCount;
        _articleBufferSize = articleBufferSize;
        _configuredMax = configuredMax;
        _rangeHeader = rangeHeader;

        if (string.IsNullOrWhiteSpace(_rangeHeader))
        {
            FileAccessLog.Logger.Information(
                "[FileAccess] Started {FileName} ({FileSizeMB:F1} MB, {SegmentCount} segments, offset {StartOffset}, buffer {ArticleBufferSize}){Range}",
                _fileName,
                _fileSize / 1024.0 / 1024.0,
                _segmentCount,
                _startOffset,
                _articleBufferSize,
                "");
        }
        else
        {
            FileAccessLog.Logger.Debug(
                "[FileAccess] Started {FileName} ({FileSizeMB:F1} MB, {SegmentCount} segments, offset {StartOffset}, buffer {ArticleBufferSize}, max {ConfiguredMax}){Range}",
                _fileName,
                _fileSize / 1024.0 / 1024.0,
                _segmentCount,
                _startOffset,
                _articleBufferSize,
                _configuredMax,
                $" range={_rangeHeader}");
        }
    }

    public void RecordSeek(long targetOffset, string strategy)
    {
        FileAccessLog.Logger.Debug(
            "[FileAccess] {FileName} seek to {Offset} ({Strategy})",
            _fileName,
            targetOffset,
            strategy);
    }

    public void RecordSeekMapResolved(long firstPartOffset, int standardPartSize)
    {
        FileAccessLog.Logger.Debug(
            "[FileAccess] {FileName} seek map resolved (firstPartOffset={FirstPartOffset}, standardPartSize={StandardPartSize})",
            _fileName,
            firstPartOffset,
            standardPartSize);
    }

    public void RecordProviderAttemptFailure(
        string providerHost,
        string operation,
        string? segmentId,
        ProviderFailureInfo failure,
        TimeSpan elapsed)
    {
        var stats = _providerStats.GetOrAdd(providerHost, static host => new ProviderSessionStats(host));
        stats.RecordAttemptFailure(
            missingArticle: failure.Category == ProviderFailureCategory.MissingArticle,
            timeout: failure.Category == ProviderFailureCategory.Timeout);

        FileAccessLog.Logger.Information(
            "[FileAccess] {FileName} provider {Provider} failed {Operation} on {SegmentId} after {ElapsedMs:F0}ms " +
            "({Category}: {Summary})",
            _fileName,
            providerHost,
            operation,
            FormatSegmentId(segmentId),
            elapsed.TotalMilliseconds,
            failure.Category,
            failure.Summary);
    }

    public void RecordProviderMissingArticle(string providerHost, string operation, string? segmentId, TimeSpan elapsed)
    {
        var stats = _providerStats.GetOrAdd(providerHost, static host => new ProviderSessionStats(host));
        stats.RecordAttemptFailure(missingArticle: true, timeout: false);

        FileAccessLog.Logger.Information(
            "[FileAccess] {FileName} provider {Provider} missing article for {Operation} on {SegmentId} after {ElapsedMs:F0}ms",
            _fileName,
            providerHost,
            operation,
            FormatSegmentId(segmentId),
            elapsed.TotalMilliseconds);
    }

    public void RecordReadAttempt()
    {
        Interlocked.Increment(ref _readAttempts);
    }

    public void RecordBytesDelivered(int bytes)
    {
        if (bytes <= 0) return;

        Interlocked.Add(ref _bytesDelivered, bytes);

        if (Interlocked.CompareExchange(ref _firstByteLogged, 1, 0) != 0) return;

        FileAccessLog.Logger.Information(
            "[FileAccess] First byte of {FileName} after {TtfbMs:F0}ms{Range}",
            _fileName,
            _stopwatch.Elapsed.TotalMilliseconds,
            string.IsNullOrWhiteSpace(_rangeHeader) ? "" : $" range={_rangeHeader}");
    }

    public void RecordSegmentSuccess(
        string providerHost,
        string? segmentId,
        int segmentIndex,
        long bytes,
        TimeSpan ttfb,
        TimeSpan transfer)
    {
        var stats = _providerStats.GetOrAdd(providerHost, static host => new ProviderSessionStats(host));
        stats.RecordSegmentSuccess(bytes, ttfb, transfer);

        var segmentsCompleted = Interlocked.Increment(ref _segmentsCompleted);

        FileAccessLog.Logger.Debug(
            "[FileAccess] {FileName} segment {SegmentIndex} from {Provider} ({SegmentId}): {Bytes} bytes, " +
            "ttfb {TtfbMs:F0}ms, transfer {TransferMs:F0}ms, {ThroughputMbps:F2} Mbit/s",
            _fileName,
            segmentIndex,
            providerHost,
            FormatSegmentId(segmentId),
            bytes,
            ttfb.TotalMilliseconds,
            transfer.TotalMilliseconds,
            transfer.TotalSeconds > 0 ? bytes * 8.0 / (transfer.TotalSeconds * 1_000_000) : 0);

        MaybeLogProgress(segmentsCompleted);
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, true)) return;

        _stopwatch.Stop();

        if (_bytesDelivered == 0 && _segmentsCompleted == 0)
        {
            var readAttempts = Interlocked.Read(ref _readAttempts);
            if (readAttempts > 0)
            {
                FileAccessLog.Logger.Debug(
                    "[FileAccess] Closed {FileName} after {ElapsedMs:F0}ms waiting for Usenet data ({ReadAttempts} read attempts, 0 bytes delivered){Range}",
                    _fileName,
                    _stopwatch.Elapsed.TotalMilliseconds,
                    readAttempts,
                    string.IsNullOrWhiteSpace(_rangeHeader) ? "" : $" range={_rangeHeader}");
            }
            else
            {
                FileAccessLog.Logger.Debug(
                    "[FileAccess] Closed {FileName} before any read after {ElapsedMs:F0}ms{Range}",
                    _fileName,
                    _stopwatch.Elapsed.TotalMilliseconds,
                    string.IsNullOrWhiteSpace(_rangeHeader) ? "" : $" range={_rangeHeader}");
            }

            return;
        }

        var snapshots = _providerStats.Values
            .Select(x => x.Snapshot())
            .OrderByDescending(x => x.ThroughputMbps)
            .ToList();

        var elapsedSeconds = Math.Max(_stopwatch.Elapsed.TotalSeconds, 0.001);
        var effectiveMbps = _bytesDelivered * 8.0 / (elapsedSeconds * 1_000_000);

        FileAccessLog.Logger.Information(
            "[FileAccess] Finished {FileName}: delivered {DeliveredMB:F1} MB in {ElapsedSeconds:F1}s " +
            "(effective {EffectiveMbps:F2} Mbit/s, {SegmentsCompleted} segments read)",
            _fileName,
            _bytesDelivered / 1024.0 / 1024.0,
            elapsedSeconds,
            effectiveMbps,
            Interlocked.Read(ref _segmentsCompleted));

        if (snapshots.Count == 0)
        {
            FileAccessLog.Logger.Information(
                "[FileAccess] {FileName}: no provider download stats recorded.",
                _fileName);
            return;
        }

        foreach (var snapshot in snapshots)
        {
            FileAccessLog.Logger.Information(
                "[FileAccess] {FileName} provider {Provider}: {SegmentsOk} segments, {BytesMB:F1} MB, " +
                "{ThroughputMbps:F2} Mbit/s avg, {AvgTtfbMs:F0}ms avg ttfb, " +
                "{AttemptFailures} failed attempts, {MissingArticles} missing, {Timeouts} timeouts",
                _fileName,
                snapshot.ProviderHost,
                snapshot.SegmentsOk,
                snapshot.Bytes / 1024.0 / 1024.0,
                snapshot.ThroughputMbps,
                snapshot.AvgTtfbMs,
                snapshot.AttemptFailures,
                snapshot.MissingArticles,
                snapshot.Timeouts);
        }
    }

    public void Cancel()
    {
        if (Interlocked.Exchange(ref _completed, true)) return;

        _stopwatch.Stop();
        FileAccessLog.Logger.Information(
            "[FileAccess] Cancelled {FileName} after {ElapsedSeconds:F1}s ({DeliveredMB:F1} MB delivered)",
            _fileName,
            _stopwatch.Elapsed.TotalSeconds,
            _bytesDelivered / 1024.0 / 1024.0);
    }

    public void Dispose()
    {
        Complete();
    }

    private void MaybeLogProgress(long segmentsCompleted)
    {
        var elapsedMs = _stopwatch.ElapsedMilliseconds;
        lock (_progressLock)
        {
            if (elapsedMs - _lastProgressLogMs < 30_000) return;
            _lastProgressLogMs = elapsedMs;
        }

        var snapshots = _providerStats.Values
            .Select(x => x.Snapshot())
            .Where(x => x.SegmentsOk > 0)
            .OrderByDescending(x => x.ThroughputMbps)
            .ToList();

        var providerSummary = snapshots.Count == 0
            ? "no provider stats yet"
            : string.Join(", ", snapshots.Select(x => $"{x.ProviderHost} {x.ThroughputMbps:F1} Mbit/s"));

        var percent = _fileSize > 0 ? 100.0 * _bytesDelivered / _fileSize : 0;

        FileAccessLog.Logger.Information(
            "[FileAccess] {FileName} progress: {Percent:F1}% ({SegmentsCompleted} segments, {DeliveredMB:F1} MB). " +
            "Providers: {ProviderSummary}",
            _fileName,
            percent,
            segmentsCompleted,
            _bytesDelivered / 1024.0 / 1024.0,
            providerSummary);
    }

    private static string FormatSegmentId(string? segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId)) return "n/a";
        return segmentId.Length <= 16 ? segmentId : segmentId[^16..];
    }
}
