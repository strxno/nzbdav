namespace NzbWebDAV.Clients.Usenet.Telemetry;

internal static class ProviderSelectionContext
{
    private static readonly AsyncLocal<string?> LastProvider = new();

    public static string? LastProviderHost
    {
        get => LastProvider.Value;
        set => LastProvider.Value = value;
    }
}
