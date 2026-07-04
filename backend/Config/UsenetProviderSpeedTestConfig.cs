namespace NzbWebDAV.Config;

public class UsenetProviderSpeedTestConfig
{
    public const string ConfigKey = "usenet.provider-speed-tests";

    public DateTime? TestedAt { get; set; }
    public List<ProviderSpeedTestEntry> Results { get; set; } = [];
}

public class ProviderSpeedTestEntry
{
    public required string Host { get; set; }
    public int ProviderIndex { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public double MegabitsPerSecond { get; set; }
    public double AverageTtfbMs { get; set; }
    public double InitialTtfbMs { get; set; }
    public double DurationSeconds { get; set; }
    public int SortRank { get; set; }
}
