namespace NzbWebDAV.Api.Controllers.TestUsenetSpeed;

public class TestUsenetSpeedResponse : BaseApiResponse
{
    public List<ProviderSpeedTestResultDto> Results { get; set; } = [];
}

public sealed class ProviderSpeedTestResultDto
{
    public int ProviderIndex { get; set; }
    public string Host { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public long BytesDownloaded { get; set; }
    public double DurationSeconds { get; set; }
    public double MegabitsPerSecond { get; set; }
    public double AverageTtfbMs { get; set; }
    public double InitialTtfbMs { get; set; }
    public int SortRank { get; set; }
}
