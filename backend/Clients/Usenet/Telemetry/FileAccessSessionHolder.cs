namespace NzbWebDAV.Clients.Usenet.Telemetry;

public sealed class FileAccessSessionHolder(FileAccessSession session)
{
    public FileAccessSession Session { get; } = session;
}
