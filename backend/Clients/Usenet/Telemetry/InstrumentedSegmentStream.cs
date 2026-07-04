using System.Diagnostics;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

internal sealed class InstrumentedSegmentStream : Stream
{
    private readonly Stream _inner;
    private readonly string _providerHost;
    private readonly string _segmentId;
    private readonly int _segmentIndex;
    private readonly TimeSpan _ttfb;
    private readonly FileAccessSession? _session;
    private readonly Stopwatch _transferStopwatch = Stopwatch.StartNew();
    private long _bytesRead;
    private bool _completed;

    public InstrumentedSegmentStream(
        Stream inner,
        string providerHost,
        string segmentId,
        int segmentIndex,
        TimeSpan ttfb,
        FileAccessSession? session)
    {
        _inner = inner;
        _providerHost = providerHost;
        _segmentId = segmentId;
        _segmentIndex = segmentIndex;
        _ttfb = ttfb;
        _session = session;
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

        if (_bytesRead <= 0) return;

        _session?.RecordSegmentSuccess(
            _providerHost,
            _segmentId,
            _segmentIndex,
            _bytesRead,
            _ttfb,
            _transferStopwatch.Elapsed);
    }
}
