using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Telemetry;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class UnbufferedMultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private readonly Memory<string> _segmentIds;
    private readonly INntpClient _usenetClient;
    private readonly int _baseSegmentIndex;
    private Stream? _stream;
    private int _currentIndex;
    private bool _disposed;

    public UnbufferedMultiSegmentStream(Memory<string> segmentIds, INntpClient usenetClient, int baseSegmentIndex = 0)
    {
        _segmentIds = segmentIds;
        _usenetClient = usenetClient;
        _baseSegmentIndex = baseSegmentIndex;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (!cancellationToken.IsCancellationRequested)
        {
            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (_currentIndex >= _segmentIds.Length) return 0;

                var segmentIndex = _baseSegmentIndex + _currentIndex;
                var segmentId = _segmentIds.Span[_currentIndex++];
                var stopwatch = Stopwatch.StartNew();
                var body = await _usenetClient.DecodedBodyAsync(segmentId, cancellationToken);
                stopwatch.Stop();
                _stream = FileAccessTelemetry.WrapSegmentStream(
                    body.Stream,
                    segmentId,
                    segmentIndex,
                    stopwatch.Elapsed,
                    cancellationToken);
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync();
            _stream = null;
        }

        return 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!disposing) return;
        _disposed = true;
        _stream?.Dispose();
        base.Dispose(disposing);
    }
}
