using System.Collections.Concurrent;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

/// <summary>
/// Process-wide provider performance scores used to order failover attempts.
/// Updated on every NNTP attempt; higher score = try sooner.
/// </summary>
public sealed class ProviderPerformanceStore
{
    private const double ResponseEmaAlpha = 0.15;

    private readonly ConcurrentDictionary<string, ProviderPerformanceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void RecordAttempt(string providerHost, ProviderAttemptOutcome outcome, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(providerHost)) return;

        _entries.GetOrAdd(providerHost, static host => new ProviderPerformanceEntry())
            .Record(outcome, elapsed.TotalMilliseconds);
    }

    public double GetSortScore(string providerHost)
    {
        if (string.IsNullOrWhiteSpace(providerHost)) return double.MinValue;
        return _entries.TryGetValue(providerHost, out var entry) ? entry.GetSortScore() : 0;
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
