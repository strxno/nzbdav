namespace NzbWebDAV.Clients.Usenet.Connections;

public enum ProviderFailureCategory
{
    Authentication,
    TooManyConnections,
    Connection,
    Timeout,
    Ssl,
    ServerError,
    Unknown
}

public readonly record struct ProviderFailureInfo(
    ProviderFailureCategory Category,
    string Summary,
    string Detail,
    int? NntpResponseCode = null,
    string? Operation = null
);
