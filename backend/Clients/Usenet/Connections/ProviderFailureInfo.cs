namespace NzbWebDAV.Clients.Usenet.Connections;

public enum ProviderFailureCategory
{
    Authentication,
    TooManyConnections,
    Connection,
    Timeout,
    Ssl,
    ServerError,
    MissingArticle,
    Unknown
}

public readonly record struct ProviderFailureInfo(
    ProviderFailureCategory Category,
    string Summary,
    string Detail,
    int? NntpResponseCode = null,
    string? Operation = null
);
