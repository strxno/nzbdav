namespace NzbWebDAV.Clients.Usenet.Telemetry;

public enum ProviderAttemptOutcome
{
    Success,
    MissingArticle,
    Failed,
    Timeout
}
