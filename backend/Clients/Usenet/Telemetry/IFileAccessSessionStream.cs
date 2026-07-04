namespace NzbWebDAV.Clients.Usenet.Telemetry;

public interface IFileAccessSessionStream
{
    FileAccessSession? FileAccessSession { get; set; }
}
