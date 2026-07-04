using Serilog;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

/// <summary>
/// File access logs use a dedicated source context so they remain visible even when
/// the global LOG_LEVEL is Warning (the Docker default).
/// </summary>
public static class FileAccessLog
{
    public const string SourceContextName = "NzbWebDAV.FileAccess";

    public static ILogger Logger { get; } = Log.ForContext("SourceContext", SourceContextName);
}
