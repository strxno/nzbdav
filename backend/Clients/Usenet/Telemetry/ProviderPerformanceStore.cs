using System.Collections.Concurrent;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

/// <summary>
/// Process-wide provider performance scores used to order failover attempts.
/// Updated on every NNTP attempt and by the provider speed test; higher score = try sooner.
/// </summary>
public sealed class ProviderPerformanceStore
{
    private const double ResponseEmaAlpha = 0.15;

    private readonly ConcurrentDictionary<string, ProviderPerformanceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, double> _speedTestMegabitsPerSecond = new(StringComparer.OrdinalIgnoreCase);

    public void RecordAttempt(string providerHost, ProviderAttemptOutcome outcome, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(providerHost)) return;

        _entries.GetOrAdd(providerHost, static _ => new ProviderPerformanceEntry())
            .Record(outcome, elapsed.TotalMilliseconds);
    }

    public void SetSpeedTestResult(string providerHost, double megabitsPerSecond, bool success)
    {
        if (string.IsNullOrWhiteSpace(providerHost)) return;

        if (success && megabitsPerSecond > 0)
        {
            _speedTestMegabitsPerSecond[providerHost] = megabitsPerSecond;
            _entries.GetOrAdd(providerHost, static _ => new ProviderPerformanceEntry())
                .ApplySpeedTestBaseline(megabitsPerSecond);
            return;
        }

        _speedTestMegabitsPerSecond.TryRemove(providerHost, out _);
        RecordAttempt(providerHost, ProviderAttemptOutcome.Failed, TimeSpan.FromSeconds(30));
    }

    public double? GetSpeedTestMegabitsPerSecond(string providerHost)
    {
        if (string.IsNullOrWhiteSpace(providerHost)) return null;
        return _speedTestMegabitsPerSecond.TryGetValue(providerHost, out var mbps) ? mbps : null;
    }

    public double GetSortScore(string providerHost)
    {
        if (string.IsNullOrWhiteSpace(providerHost)) return double.MinValue;

        var speedTestBonus = _speedTestMegabitsPerSecond.TryGetValue(providerHost, out var mbps)
            ? mbps * 10_000
            : 0;

        var runtimeScore = _entries.TryGetValue(providerHost, out var entry) ? entry.GetSortScore() : 0;
        return speedTestBonus + runtimeScore;
    }

    public IReadOnlyList<ProviderPerformanceRanking> GetRankings(IEnumerable<string> providerHosts)
    {
        return providerHosts
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(host => new ProviderPerformanceRanking(
                host,
                GetSortScore(host),
                GetSpeedTestMegabitsPerSecond(host)))
            .OrderByDescending(x => x.SortScore)
            .Select((ranking, index) => ranking with { Rank = index + 1 })
            .ToList();
    }

    private sealed class ProviderPerformanceEntry
    {
        private long _successes;
        private long _missingArticles;
        private long _failures;
        private long _timeouts;
        private readonly object _emaLock = new();
        private double _emaResponseMs = 500;

        public void Record(ProviderAttemptOutcome outcome, double elapsedMs)
        {
            switch (outcome)
            {
                case ProviderAttemptOutcome.Success:
                    Interlocked.Increment(ref _successes);
                    break;
                case ProviderAttemptOutcome.MissingArticle:
                    Interlocked.Increment(ref _missingArticles);
                    break;
                case ProviderAttemptOutcome.Timeout:
                    Interlocked.Increment(ref _timeouts);
                    break;
                default:
                    Interlocked.Increment(ref _failures);
                    break;
            }

            UpdateResponseEma(elapsedMs);
        }

        public void ApplySpeedTestBaseline(double megabitsPerSecond)
        {
            lock (_emaLock)
            {
                _successes = 100;
                _missingArticles = 0;
                _failures = 0;
                _timeouts = 0;
                _emaResponseMs = Math.Max(50, 700_000 * 8 / Math.Max(1, megabitsPerSecond));
            }
        }

        private void UpdateResponseEma(double elapsedMs)
        {
            lock (_emaLock)
            {
                _emaResponseMs = _emaResponseMs <= 0
                    ? elapsedMs
                    : _emaResponseMs + ResponseEmaAlpha * (elapsedMs - _emaResponseMs);
            }
        }

        public double GetSortScore()
        {
            var successes = Volatile.Read(ref _successes);
            var missing = Volatile.Read(ref _missingArticles);
            var failures = Volatile.Read(ref _failures);
            var timeouts = Volatile.Read(ref _timeouts);
            var total = successes + missing + failures + timeouts;
            if (total == 0) return 0;

            double responseMs;
            lock (_emaLock)
            {
                responseMs = _emaResponseMs;
            }

            var successRate = (double)successes / total;
            responseMs = Math.Max(50, responseMs);
            var penalty = missing * 0.35 + failures * 0.75 + timeouts * 1.5;

            return successRate * 10_000 / responseMs - penalty;
        }
    }
}

public sealed record ProviderPerformanceRanking(
    string Host,
    double SortScore,
    double? SpeedTestMegabitsPerSecond,
    int Rank = 0);
