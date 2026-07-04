namespace NzbWebDAV.Clients.Usenet.Telemetry;

internal sealed class ProviderSessionStats
{
    private long _bytes;
    private int _segmentsOk;
    private int _attemptFailures;
    private int _missingArticles;
    private int _timeouts;
    private long _totalTtfbTicks;
    private long _totalTransferTicks;

    public string ProviderHost { get; }

    public ProviderSessionStats(string providerHost)
    {
        ProviderHost = providerHost;
    }

    public void RecordAttemptFailure(bool missingArticle, bool timeout)
    {
        Interlocked.Increment(ref _attemptFailures);
        if (missingArticle) Interlocked.Increment(ref _missingArticles);
        if (timeout) Interlocked.Increment(ref _timeouts);
    }

    public void RecordSegmentSuccess(long bytes, TimeSpan ttfb, TimeSpan transfer)
    {
        Interlocked.Increment(ref _segmentsOk);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _totalTtfbTicks, ttfb.Ticks);
        Interlocked.Add(ref _totalTransferTicks, transfer.Ticks);
    }

    public ProviderStatsSnapshot Snapshot()
    {
        var bytes = Interlocked.Read(ref _bytes);
        var segmentsOk = Volatile.Read(ref _segmentsOk);
        var attemptFailures = Volatile.Read(ref _attemptFailures);
        var missingArticles = Volatile.Read(ref _missingArticles);
        var timeouts = Volatile.Read(ref _timeouts);
        var totalTtfbTicks = Interlocked.Read(ref _totalTtfbTicks);
        var totalTransferTicks = Interlocked.Read(ref _totalTransferTicks);

        var avgTtfbMs = segmentsOk > 0 ? TimeSpan.FromTicks(totalTtfbTicks / segmentsOk).TotalMilliseconds : 0;
        var throughputMbps = totalTransferTicks > 0
            ? bytes * 8.0 / (TimeSpan.FromTicks(totalTransferTicks).TotalSeconds * 1_000_000)
            : 0;

        return new ProviderStatsSnapshot(
            ProviderHost,
            segmentsOk,
            attemptFailures,
            missingArticles,
            timeouts,
            bytes,
            avgTtfbMs,
            throughputMbps);
    }
}

internal readonly record struct ProviderStatsSnapshot(
    string ProviderHost,
    long SegmentsOk,
    long AttemptFailures,
    long MissingArticles,
    long Timeouts,
    long Bytes,
    double AvgTtfbMs,
    double ThroughputMbps);
