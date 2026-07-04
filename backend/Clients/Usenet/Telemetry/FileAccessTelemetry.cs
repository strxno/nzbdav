using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

public static class FileAccessTelemetry
{
    public static FileAccessSession? ResolveSession(CancellationToken cancellationToken = default)
    {
        return cancellationToken.GetContext<FileAccessSessionHolder>()?.Session;
    }

    public static void RecordSeek(CancellationToken cancellationToken, long targetOffset, string strategy)
    {
        ResolveSession(cancellationToken)?.RecordSeek(targetOffset, strategy);
    }

    public static void RecordSeekMapResolved(CancellationToken cancellationToken, long firstPartOffset, int standardPartSize)
    {
        ResolveSession(cancellationToken)?.RecordSeekMapResolved(firstPartOffset, standardPartSize);
    }

    public static void RecordProviderAttemptFailure(
        CancellationToken cancellationToken,
        string providerHost,
        string operation,
        string? segmentId,
        ProviderFailureInfo failure,
        TimeSpan elapsed)
    {
        ResolveSession(cancellationToken)?.RecordProviderAttemptFailure(
            providerHost, operation, segmentId, failure, elapsed);
    }

    public static void RecordProviderMissingArticle(
        CancellationToken cancellationToken,
        string providerHost,
        string operation,
        string? segmentId,
        TimeSpan elapsed)
    {
        ResolveSession(cancellationToken)?.RecordProviderMissingArticle(
            providerHost, operation, segmentId, elapsed);
    }

    public static Stream WrapSegmentStream(
        Stream inner,
        string providerHost,
        string segmentId,
        int segmentIndex,
        TimeSpan ttfb,
        FileAccessSession? session)
    {
        return new InstrumentedSegmentStream(
            inner, providerHost, segmentId, segmentIndex, ttfb, session);
    }

    public static void BindSession(Stream stream, FileAccessSession session)
    {
        if (TryBindSession(stream, session)) return;

        FileAccessLog.Logger.Debug(
            "[FileAccess] Could not bind telemetry session to {StreamType}",
            stream.GetType().Name);
    }

    private static bool TryBindSession(Stream stream, FileAccessSession session)
    {
        if (stream is not IFileAccessSessionStream target) return false;
        target.FileAccessSession = session;
        return true;
    }
}
