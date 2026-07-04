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
        string segmentId,
        int segmentIndex,
        TimeSpan ttfb,
        CancellationToken cancellationToken)
    {
        var providerHost = ProviderSelectionContext.LastProviderHost ?? "unknown";
        var session = ResolveSession(cancellationToken);
        return new InstrumentedSegmentStream(
            inner, providerHost, segmentId, segmentIndex, ttfb, session);
    }
}
