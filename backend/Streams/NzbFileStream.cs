using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Telemetry;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize,
    long firstPartOffset = 0,
    int standardPartSize = 0,
    Func<CancellationToken, Task<(long FirstPartOffset, int StandardPartSize)>>? resolveSeekMapAsync = null
) : FastReadOnlyStream, IFileAccessSessionStream
{
    private long _firstPartOffset = firstPartOffset;
    private int _standardPartSize = standardPartSize;
    private bool _seekMapResolveAttempted;
    private long _position;
    private bool _disposed;
    private Stream? _innerStream;
    private CancellationToken _telemetryCancellationToken;

    public FileAccessSession? FileAccessSession { get; set; }

    private bool HasSeekMap => _standardPartSize > 0 && fileSegmentIds.Length > 0;

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _telemetryCancellationToken = cancellationToken;
        if (_position >= fileSize) return 0;
        _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset) return _position;
        var strategy = HasSeekMap ? "seek-map" : "interpolation-search";
        FileAccessTelemetry.RecordSeek(_telemetryCancellationToken, absoluteOffset, strategy);
        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        return _position;
    }

    private async Task EnsureSeekMapAsync(CancellationToken cancellationToken)
    {
        if (HasSeekMap || _seekMapResolveAttempted || resolveSeekMapAsync is null)
            return;

        _seekMapResolveAttempted = true;
        var map = await resolveSeekMapAsync(cancellationToken).ConfigureAwait(false);
        _firstPartOffset = map.FirstPartOffset;
        _standardPartSize = map.StandardPartSize;
        FileAccessTelemetry.RecordSeekMapResolved(_telemetryCancellationToken, _firstPartOffset, _standardPartSize);
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async (guess) =>
            {
                var header = await usenetClient.GetYencHeadersAsync(fileSegmentIds[guess], ct).ConfigureAwait(false);
                return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
            },
            ct
        ).ConfigureAwait(false);
    }

    private async Task<Stream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        if (rangeStart == 0) return GetMultiSegmentStream(0, cancellationToken);

        if (!HasSeekMap)
            await EnsureSeekMapAsync(cancellationToken).ConfigureAwait(false);

        if (HasSeekMap)
        {
            var segmentIndex = SegmentSeekMap.FindSegmentIndex(
                rangeStart,
                fileSize,
                fileSegmentIds.Length,
                _firstPartOffset,
                _standardPartSize);
            var segmentStart = SegmentSeekMap.GetSegmentStartOffset(segmentIndex, _firstPartOffset, _standardPartSize);
            var stream = GetMultiSegmentStream(segmentIndex, cancellationToken);
            await stream.DiscardBytesAsync(rangeStart - segmentStart, cancellationToken).ConfigureAwait(false);
            return stream;
        }

        var foundSegment = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        var fallbackStream = GetMultiSegmentStream(foundSegment.FoundIndex, cancellationToken);
        await fallbackStream
            .DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive, cancellationToken)
            .ConfigureAwait(false);
        return fallbackStream;
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, CancellationToken cancellationToken)
    {
        var segmentIds = fileSegmentIds.AsMemory()[firstSegmentIndex..];
        return MultiSegmentStream.Create(
            segmentIds,
            usenetClient,
            articleBufferSize,
            cancellationToken,
            firstSegmentIndex,
            FileAccessSession);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _innerStream?.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
