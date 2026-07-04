using System.Diagnostics;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

public static class FileAccessTelemetry
{
    private static readonly AsyncLocal<FileAccessSession?> Current = new();

    public static FileAccessSession? CurrentSession => Current.Value;

    public static FileAccessSession Begin(
        string fileName,
        long fileSize,
        long startOffset,
        int segmentCount,
        int articleBufferSize,
        string? rangeHeader = null)
    {
        var session = new FileAccessSession(
            fileName,
            fileSize,
            startOffset,
            segmentCount,
            articleBufferSize,
            rangeHeader);
        Current.Value = session;
        return session;
    }

    public static IDisposable BeginScope(
        string fileName,
        long fileSize,
        long startOffset,
        int segmentCount,
        int articleBufferSize,
        string? rangeHeader = null)
    {
        return new FileAccessSessionScope(Begin(fileName, fileSize, startOffset, segmentCount, articleBufferSize, rangeHeader));
    }

    internal static void ClearSession(FileAccessSession session)
    {
        if (ReferenceEquals(Current.Value, session))
            Current.Value = null;
    }

    public static void RecordSeek(long targetOffset, string strategy)
    {
        CurrentSession?.RecordSeek(targetOffset, strategy);
    }

    public static void RecordSeekMapResolved(long firstPartOffset, int standardPartSize)
    {
        CurrentSession?.RecordSeekMapResolved(firstPartOffset, standardPartSize);
    }

    public static void RecordProviderAttemptFailure(
        string providerHost,
        string operation,
        string? segmentId,
        ProviderFailureInfo failure,
        TimeSpan elapsed)
    {
        CurrentSession?.RecordProviderAttemptFailure(providerHost, operation, segmentId, failure, elapsed);
    }

    public static void RecordProviderMissingArticle(
        string providerHost,
        string operation,
        string? segmentId,
        TimeSpan elapsed)
    {
        CurrentSession?.RecordProviderMissingArticle(providerHost, operation, segmentId, elapsed);
    }

    public static Stream WrapSegmentStream(Stream inner, string segmentId, int segmentIndex, TimeSpan ttfb)
    {
        var providerHost = ProviderSelectionContext.LastProviderHost ?? "unknown";
        return new InstrumentedSegmentStream(inner, providerHost, segmentId, segmentIndex, ttfb);
    }
}

internal sealed class InstrumentedSegmentStream : Stream
{
    private readonly Stream _inner;
    private readonly string _providerHost;
    private readonly string _segmentId;
    private readonly int _segmentIndex;
    private readonly TimeSpan _ttfb;
    private readonly Stopwatch _transferStopwatch = Stopwatch.StartNew();
    private long _bytesRead;
    private bool _completed;

    public InstrumentedSegmentStream(
        Stream inner,
        string providerHost,
        string segmentId,
        int segmentIndex,
        TimeSpan ttfb)
    {
        _inner = inner;
        _providerHost = providerHost;
        _segmentId = segmentId;
        _segmentIndex = segmentIndex;
        _ttfb = ttfb;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        _bytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _bytesRead += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteSegment();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        CompleteSegment();
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void CompleteSegment()
    {
        if (_completed) return;
        _completed = true;
        _transferStopwatch.Stop();

        FileAccessTelemetry.CurrentSession?.RecordSegmentSuccess(
            _providerHost,
            _segmentId,
            _segmentIndex,
            _bytesRead,
            _ttfb,
            _transferStopwatch.Elapsed);
    }
}
