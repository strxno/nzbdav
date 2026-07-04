using NzbWebDAV.Clients.Usenet.Telemetry;

namespace NzbWebDAV.Streams;

public sealed class FileAccessLoggingStream(Stream innerStream, FileAccessSession session) : Stream
{
    private readonly Stream _innerStream = innerStream;
    private readonly FileAccessSession _session = session;
    private bool _disposed;

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;
    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        _session.RecordReadAttempt();
        var read = _innerStream.Read(buffer, offset, count);
        _session.RecordBytesDelivered(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        _session.RecordReadAttempt();
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _session.RecordBytesDelivered(read);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _innerStream.Dispose();
            _session.Complete();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _innerStream.DisposeAsync().ConfigureAwait(false);
        _session.Complete();
        _disposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
