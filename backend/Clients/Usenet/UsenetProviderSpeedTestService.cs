using System.Diagnostics;
using System.Reflection;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Telemetry;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Clients.Usenet;

public sealed class UsenetProviderSpeedTestService(
    ConfigManager configManager,
    ProviderPerformanceStore performanceStore,
    WebsocketManager websocketManager)
{
    private const long TargetBytes = 500L * 1024 * 1024;
    private const string SpeedTestNzbResourceName = "NzbWebDAV.Resources.NZBGet-Speed-Test-500MB.nzb";

    private readonly SemaphoreSlim _runLock = new(1, 1);

    public bool IsRunning => _runLock.CurrentCount == 0;

    public async Task<IReadOnlyList<ProviderSpeedTestResult>> RunAllAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A provider speed test is already running.");

        try
        {
            var providerConfig = configManager.GetUsenetProviderConfig();
            var segments = await LoadSpeedTestSegmentsAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<ProviderSpeedTestResult>();
            var totalProviders = providerConfig.Providers.Count;
            var completedProviders = 0;

            for (var index = 0; index < providerConfig.Providers.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = providerConfig.Providers[index];
                if (provider.Type == ProviderType.Disabled)
                {
                    results.Add(new ProviderSpeedTestResult
                    {
                        ProviderIndex = index,
                        Host = provider.Host,
                        Success = false,
                        Error = "Provider is disabled."
                    });
                    continue;
                }

                var completedBefore = completedProviders;
                await SendProgressAsync(completedBefore, totalProviders, provider.Host, 0, cancellationToken)
                    .ConfigureAwait(false);

                var result = await TestProviderAsync(
                        index,
                        completedBefore,
                        totalProviders,
                        provider,
                        segments,
                        cancellationToken)
                    .ConfigureAwait(false);
                results.Add(result);
                completedProviders++;

                performanceStore.SetSpeedTestResult(
                    provider.Host,
                    result.MegabitsPerSecond,
                    result.Success);

                await SendProgressAsync(
                        completedProviders,
                        totalProviders,
                        provider.Host,
                        100,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var rankedHosts = performanceStore
                .GetRankings(providerConfig.Providers.Select(x => x.Host))
                .ToDictionary(x => x.Host, x => x.Rank, StringComparer.OrdinalIgnoreCase);

            return results
                .Select(x => x with { SortRank = rankedHosts.GetValueOrDefault(x.Host) })
                .OrderBy(x => x.SortRank == 0 ? int.MaxValue : x.SortRank)
                .ToList();
        }
        finally
        {
            _runLock.Release();
            _ = websocketManager.SendMessage(WebsocketTopic.UsenetSpeedTestProgress, "done|");
        }
    }

    private async Task<ProviderSpeedTestResult> TestProviderAsync
    (
        int providerIndex,
        int completedProviders,
        int totalProviders,
        UsenetProviderConfig.ConnectionDetails provider,
        IReadOnlyList<SpeedTestSegment> segments,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        long bytesDownloaded = 0;

        using var client = CreateSingleProviderClient(provider);
        var concurrency = Math.Max(1, provider.MaxConnections);

        try
        {
            var tasks = segments.Select(async segment =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await client.DecodedBodyAsync(segment.MessageId, cancellationToken)
                    .ConfigureAwait(false);
                await using var stream = response.Stream;
                await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                return segment.Bytes;
            });

            var completed = 0;
            await foreach (var bytes in tasks.WithConcurrencyAsync(concurrency).ConfigureAwait(false))
            {
                bytesDownloaded += bytes;
                completed++;
                if (completed % 25 == 0 || completed == segments.Count)
                {
                    var percent = (int)Math.Clamp(100.0 * completed / segments.Count, 0, 100);
                    await SendProgressAsync(
                            completedProviders,
                            totalProviders,
                            provider.Host,
                            percent,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            stopwatch.Stop();
            Log.Warning(e, "Provider speed test failed for {Host}", provider.Host);
            return new ProviderSpeedTestResult
            {
                ProviderIndex = providerIndex,
                Host = provider.Host,
                Success = false,
                Error = e.Message,
                BytesDownloaded = bytesDownloaded,
                DurationSeconds = stopwatch.Elapsed.TotalSeconds,
                MegabitsPerSecond = CalculateMegabitsPerSecond(bytesDownloaded, stopwatch.Elapsed)
            };
        }

        stopwatch.Stop();
        var megabitsPerSecond = CalculateMegabitsPerSecond(bytesDownloaded, stopwatch.Elapsed);
        Log.Information(
            "Provider speed test {Host}: {MegabitsPerSecond:F1} Mbit/s ({Megabytes:F1} MB in {Seconds:F1}s)",
            provider.Host,
            megabitsPerSecond,
            bytesDownloaded / 1024d / 1024d,
            stopwatch.Elapsed.TotalSeconds);

        return new ProviderSpeedTestResult
        {
            ProviderIndex = providerIndex,
            Host = provider.Host,
            Success = bytesDownloaded > 0,
            BytesDownloaded = bytesDownloaded,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            MegabitsPerSecond = megabitsPerSecond
        };
    }

    private static MultiConnectionNntpClient CreateSingleProviderClient(UsenetProviderConfig.ConnectionDetails provider)
    {
        var connectionPool = UsenetStreamingClient.CreateNewConnectionPool(
            maxConnections: provider.MaxConnections,
            connectionFactory: ct => UsenetStreamingClient.CreateNewConnection(provider, ct),
            onConnectionPoolChanged: (_, _) => { });

        var circuitBreaker = new ProviderCircuitBreaker(provider.Host);
        return new MultiConnectionNntpClient(
            connectionPool,
            ProviderType.Pooled,
            circuitBreaker,
            provider.Host);
    }

    private static async Task<IReadOnlyList<SpeedTestSegment>> LoadSpeedTestSegmentsAsync(CancellationToken cancellationToken)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SpeedTestNzbResourceName)
            ?? throw new InvalidOperationException("Embedded NZBGet speed test NZB was not found.");

        var nzb = await NzbDocument.LoadAsync(stream).ConfigureAwait(false);
        var segments = new List<SpeedTestSegment>();
        long totalBytes = 0;

        foreach (var file in nzb.Files)
        {
            if (file.Subject.Contains(".par2", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var segment in file.Segments)
            {
                if (totalBytes >= TargetBytes)
                    return segments;

                segments.Add(new SpeedTestSegment(segment.MessageId, segment.Bytes));
                totalBytes += segment.Bytes;
            }
        }

        if (segments.Count == 0)
            throw new InvalidOperationException("NZBGet speed test NZB contains no downloadable segments.");

        return segments;
    }

    private Task SendProgressAsync(
        int completedProviders,
        int totalProviders,
        string host,
        int percent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = $"{completedProviders}|{totalProviders}|{host}|{percent}";
        return websocketManager.SendMessage(WebsocketTopic.UsenetSpeedTestProgress, message);
    }

    private static double CalculateMegabitsPerSecond(long bytes, TimeSpan elapsed)
    {
        if (bytes <= 0 || elapsed.TotalSeconds <= 0) return 0;
        return bytes * 8d / elapsed.TotalSeconds / 1_000_000d;
    }

    private sealed record SpeedTestSegment(string MessageId, long Bytes);
}

public sealed record ProviderSpeedTestResult
{
    public required int ProviderIndex { get; init; }
    public required string Host { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long BytesDownloaded { get; init; }
    public double DurationSeconds { get; init; }
    public double MegabitsPerSecond { get; init; }
    public int SortRank { get; init; }
}
