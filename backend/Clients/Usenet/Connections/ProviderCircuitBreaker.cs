using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Tracks consecutive connection failures for an NNTP provider and temporarily
/// disables it when a failure threshold is reached, preventing a single
/// misbehaving provider from blocking the entire download pipeline.
/// <para>
/// After tripping, the provider enters a cooldown period during which it is
/// skipped. When the cooldown expires, a single probe attempt is allowed.
/// If the probe succeeds, the breaker resets. If it fails, the cooldown
/// doubles (up to a cap) and the breaker re-trips.
/// </para>
/// </summary>
public class ProviderCircuitBreaker
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    private readonly string _providerName;
    private readonly object _lock = new();
    private readonly List<ProviderFailureInfo> _recentFailures = [];

    private int _consecutiveFailures;
    private long _trippedUntilMs;
    private TimeSpan _currentCooldown = InitialCooldown;
    private ProviderFailureInfo? _lastFailure;

    public ProviderCircuitBreaker(string providerName)
    {
        _providerName = providerName;
    }

    public bool IsTripped
    {
        get
        {
            lock (_lock)
            {
                ClearExpiredCooldownLocked();
                return IsTrippedLocked();
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0 || _trippedUntilMs > 0)
                Log.Information("Provider {Provider} recovered — circuit breaker reset.", _providerName);

            _consecutiveFailures = 0;
            _trippedUntilMs = 0;
            _currentCooldown = InitialCooldown;
            _recentFailures.Clear();
            _lastFailure = null;
        }
    }

    public void RecordFailure(Exception exception, string? operation = null)
    {
        var failure = ProviderFailureClassifier.Classify(exception, operation);

        lock (_lock)
        {
            ClearExpiredCooldownLocked();

            // Parallel in-flight requests can all fail together; only trip once per cooldown.
            if (IsTrippedLocked()) return;

            _lastFailure = failure;
            _recentFailures.Add(failure);
            if (_recentFailures.Count > FailureThreshold)
                _recentFailures.RemoveAt(0);

            _consecutiveFailures++;

            Log.Debug(
                "Provider {Provider} failure {FailureCount}/{FailureThreshold} during {Operation}: " +
                "{Category} — {Summary}. Detail: {Detail}",
                _providerName,
                _consecutiveFailures,
                FailureThreshold,
                failure.Operation ?? operation ?? "NNTP",
                failure.Category,
                failure.Summary,
                failure.Detail);

            if (_consecutiveFailures < FailureThreshold) return;

            TripLocked();
        }
    }

    private void ClearExpiredCooldownLocked()
    {
        if (_trippedUntilMs == 0 || Environment.TickCount64 < _trippedUntilMs) return;

        _trippedUntilMs = 0;
        _consecutiveFailures = 0;
        _recentFailures.Clear();
        _lastFailure = null;
    }

    private bool IsTrippedLocked()
    {
        return _trippedUntilMs > 0 && Environment.TickCount64 < _trippedUntilMs;
    }

    private void TripLocked()
    {
        var cooldown = _currentCooldown;
        _trippedUntilMs = Environment.TickCount64 + (long)cooldown.TotalMilliseconds;

        var lastFailure = _lastFailure;
        var recentCategories = string.Join(", ", _recentFailures.Select(x => x.Category));

        if (lastFailure is null)
        {
            Log.Warning(
                "Provider {Provider} tripped after {Failures} consecutive failures. Skipping for {Cooldown}s.",
                _providerName, _consecutiveFailures, cooldown.TotalSeconds);
        }
        else
        {
            Log.Warning(
                "Provider {Provider} tripped after {Failures} consecutive failures. " +
                "Reason: {Category} — {Summary}. " +
                "Operation: {Operation}. NNTP code: {NntpCode}. Detail: {Detail}. " +
                "Recent failures: {RecentCategories}. Skipping for {Cooldown}s.",
                _providerName,
                _consecutiveFailures,
                lastFailure.Value.Category,
                lastFailure.Value.Summary,
                lastFailure.Value.Operation ?? "NNTP",
                lastFailure.Value.NntpResponseCode?.ToString() ?? "n/a",
                lastFailure.Value.Detail,
                recentCategories,
                cooldown.TotalSeconds);
        }

        _currentCooldown = TimeSpan.FromMilliseconds(
            Math.Min(cooldown.TotalMilliseconds * 2, MaxCooldown.TotalMilliseconds));
    }
}
