namespace NzbWebDAV.Clients.Usenet.Telemetry;

internal sealed class ProviderSessionStats
{
    private long _bytes;
    private int _segmentsOk;
    private int _attemptFailures;
    private int _missingArticles;
    private int _timeouts;
    private int _successfulAttempts;
    private long _totalTtfbTicks;
    private long _totalTransferTicks;
    private long _totalResponseTicks;
    private long _successfulResponseTicks;

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

    public void RecordSuccessfulAttempt(TimeSpan responseTime)
    {
        Interlocked.Increment(ref _successfulAttempts);
        Interlocked.Add(ref _successfulResponseTicks, responseTime.Ticks);
    }

    public void RecordAttemptResponseTime(TimeSpan responseTime)
    {
        Interlocked.Add(ref _totalResponseTicks, responseTime.Ticks);
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
        var successfulAttempts = Volatile.Read(ref _successfulAttempts);
        var totalAttempts = successfulAttempts + attemptFailures;
        var totalTtfbTicks = Interlocked.Read(ref _totalTtfbTicks);
        var totalTransferTicks = Interlocked.Read(ref _totalTransferTicks);
        var totalResponseTicks = Interlocked.Read(ref _totalResponseTicks);
        var successfulResponseTicks = Interlocked.Read(ref _successfulResponseTicks);

        var avgTtfbMs = segmentsOk > 0 ? TimeSpan.FromTicks(totalTtfbTicks / segmentsOk).TotalMilliseconds : 0;
        var throughputMbps = totalTransferTicks > 0
            ? bytes * 8.0 / (TimeSpan.FromTicks(totalTransferTicks).TotalSeconds * 1_000_000)
            : 0;
        var avgResponseMs = totalAttempts > 0
            ? TimeSpan.FromTicks(totalResponseTicks / totalAttempts).TotalMilliseconds
            : 0;
        var avgSuccessResponseMs = successfulAttempts > 0
            ? TimeSpan.FromTicks(successfulResponseTicks / successfulAttempts).TotalMilliseconds
            : 0;

        return new ProviderStatsSnapshot(
            ProviderHost,
            segmentsOk,
            attemptFailures,
            missingArticles,
            timeouts,
            successfulAttempts,
            bytes,
            avgTtfbMs,
            throughputMbps,
            avgResponseMs,
            avgSuccessResponseMs);
    }
}

internal readonly record struct ProviderStatsSnapshot(
    string ProviderHost,
    long SegmentsOk,
    long AttemptFailures,
    long MissingArticles,
    long Timeouts,
    long SuccessfulAttempts,
    long Bytes,
    double AvgTtfbMs,
    double ThroughputMbps,
    double AvgResponseMs,
    double AvgSuccessResponseMs);
