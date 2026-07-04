namespace NzbWebDAV.Clients.Usenet.Telemetry;

internal sealed class FileAccessSessionScope(FileAccessSession session) : IDisposable
{
    public void Dispose()
    {
        session.Complete();
    }
}
