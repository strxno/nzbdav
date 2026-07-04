using System.Collections.Concurrent;
using System.Diagnostics;
using NzbWebDAV.Clients.Usenet.Connections;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

public sealed class FileAccessSession : IDisposable
{
    private readonly string _fileName;
    private readonly long _fileSize;
    private readonly long _startOffset;
    private readonly int _segmentCount;
    private readonly int _articleBufferSize;
    private readonly string? _rangeHeader;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, ProviderSessionStats> _providerStats = new();
    private readonly object _progressLock = new();

    private long _bytesDelivered;
    private long _segmentsCompleted;
    private long _lastProgressLogMs;
    private bool _completed;

    public FileAccessSession(
        string fileName,
        long fileSize,
        long startOffset,
        int segmentCount,
        int articleBufferSize,
        string? rangeHeader)
    {
        _fileName = fileName;
        _fileSize = fileSize;
        _startOffset = startOffset;
        _segmentCount = segmentCount;
        _articleBufferSize = articleBufferSize;
        _rangeHeader = rangeHeader;

        Log.Information(
            "[FileAccess] Started {FileName} ({FileSizeMB:F1} MB, {SegmentCount} segments, offset {StartOffset}, buffer {ArticleBufferSize}){Range}",
            _fileName,
            _fileSize / 1024.0 / 1024.0,
            _segmentCount,
            _startOffset,
            _articleBufferSize,
            string.IsNullOrWhiteSpace(_rangeHeader) ? "" : $" range={_rangeHeader}");
    }

    public void RecordSeek(long targetOffset, string strategy)
    {
        Log.Debug(
            "[FileAccess] {FileName} seek to {Offset} ({Strategy})",
            _fileName,
            targetOffset,
            strategy);
    }

    public void RecordSeekMapResolved(long firstPartOffset, int standardPartSize)
    {
        Log.Debug(
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

        Log.Information(
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

        Log.Information(
            "[FileAccess] {FileName} provider {Provider} missing article for {Operation} on {SegmentId} after {ElapsedMs:F0}ms",
            _fileName,
            providerHost,
            operation,
            FormatSegmentId(segmentId),
            elapsed.TotalMilliseconds);
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

        Interlocked.Add(ref _bytesDelivered, bytes);
        var segmentsCompleted = Interlocked.Increment(ref _segmentsCompleted);

        Log.Debug(
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
        var snapshots = _providerStats.Values
            .Select(x => x.Snapshot())
            .OrderByDescending(x => x.ThroughputMbps)
            .ToList();

        var elapsedSeconds = Math.Max(_stopwatch.Elapsed.TotalSeconds, 0.001);
        var effectiveMbps = _bytesDelivered * 8.0 / (elapsedSeconds * 1_000_000);

        Log.Information(
            "[FileAccess] Finished {FileName}: delivered {DeliveredMB:F1} MB in {ElapsedSeconds:F1}s " +
            "(effective {EffectiveMbps:F2} Mbit/s, {SegmentsCompleted} segments read)",
            _fileName,
            _bytesDelivered / 1024.0 / 1024.0,
            elapsedSeconds,
            effectiveMbps,
            _segmentsCompleted);

        if (snapshots.Count == 0)
        {
            Log.Information("[FileAccess] {FileName}: no provider download stats recorded.", _fileName);
            return;
        }

        foreach (var snapshot in snapshots)
        {
            Log.Information(
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
        Log.Information(
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

        Log.Information(
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
