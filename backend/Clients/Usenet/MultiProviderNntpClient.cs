using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Clients.Usenet.Telemetry;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(
    List<MultiConnectionNntpClient> providers,
    ProviderPerformanceStore performanceStore) : NntpClient
{
    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            x => x.StatAsync(segmentId, cancellationToken),
            cancellationToken,
            "STAT",
            segmentId);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            x => x.HeadAsync(segmentId, cancellationToken),
            cancellationToken,
            "HEAD",
            segmentId);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(
            x => x.DecodedBodyAsync(segmentId, cancellationToken),
            cancellationToken,
            "BODY",
            segmentId);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(
            x => x.DecodedArticleAsync(segmentId, cancellationToken),
            cancellationToken,
            "ARTICLE",
            segmentId);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken, "DATE");
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        var providerBody = await DecodedBodyFromProviderAsync(
            segmentId,
            onConnectionReadyAgain,
            cancellationToken,
            telemetrySession: null).ConfigureAwait(false);
        return providerBody.Response;
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken,
                "ARTICLE",
                segmentId
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override Task<UsenetProviderBodyResponse> DecodedBodyFromProviderAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken,
        FileAccessSession? telemetrySession = null
    )
    {
        return DecodedBodyFromProviderAsync(segmentId, onConnectionReadyAgain: null, cancellationToken, telemetrySession);
    }

    public async Task<UsenetProviderBodyResponse> DecodedBodyFromProviderAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken,
        FileAccessSession? telemetrySession = null
    )
    {
        try
        {
            var (result, providerHost) = await RunFromPoolWithProviderAsync(
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken,
                telemetrySession,
                "BODY",
                segmentId).ConfigureAwait(false);

            if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

            return new UsenetProviderBodyResponse(result, providerHost);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken,
        string operation,
        string? segmentId = null
    ) where T : UsenetResponse
    {
        var (result, _) = await RunFromPoolWithProviderAsync(
            task,
            cancellationToken,
            telemetrySession: null,
            operation,
            segmentId).ConfigureAwait(false);
        return result;
    }

    private async Task<(T Result, string ProviderHost)> RunFromPoolWithProviderAsync<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken,
        FileAccessSession? telemetrySession,
        string operation,
        string? segmentId = null
    ) where T : UsenetResponse
    {
        telemetrySession ??= FileAccessTelemetry.ResolveSession(cancellationToken);
        ExceptionDispatchInfo? lastException = null;
        var orderedProviders = GetOrderedProviders();
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);
                stopwatch.Stop();

                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    performanceStore.RecordAttempt(
                        provider.ProviderHost,
                        ProviderAttemptOutcome.MissingArticle,
                        stopwatch.Elapsed);
                    telemetrySession?.RecordProviderAttempt(
                        provider.ProviderHost,
                        operation,
                        segmentId,
                        stopwatch.Elapsed,
                        ProviderAttemptOutcome.MissingArticle);
                    continue;
                }

                performanceStore.RecordAttempt(
                    provider.ProviderHost,
                    ProviderAttemptOutcome.Success,
                    stopwatch.Elapsed);
                telemetrySession?.RecordProviderAttempt(
                    provider.ProviderHost,
                    operation,
                    segmentId,
                    stopwatch.Elapsed,
                    ProviderAttemptOutcome.Success);

                if (lastException is not null && telemetrySession is not null)
                {
                    FileAccessLog.Logger.Information(
                        "[FileAccess] {Operation} on {SegmentId} succeeded on provider {Provider} after failover in {ElapsedMs:F0}ms",
                        operation,
                        FormatSegmentId(segmentId),
                        provider.ProviderHost,
                        stopwatch.Elapsed.TotalMilliseconds);
                }

                return (result, provider.ProviderHost);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                var failure = ProviderFailureClassifier.Classify(e, operation);
                var outcome = failure.Category switch
                {
                    ProviderFailureCategory.MissingArticle => ProviderAttemptOutcome.MissingArticle,
                    ProviderFailureCategory.Timeout => ProviderAttemptOutcome.Timeout,
                    _ => ProviderAttemptOutcome.Failed
                };
                performanceStore.RecordAttempt(provider.ProviderHost, outcome, stopwatch.Elapsed);
                if (telemetrySession is not null)
                {
                    telemetrySession.RecordProviderAttemptFailure(
                        provider.ProviderHost,
                        operation,
                        segmentId,
                        failure,
                        stopwatch.Elapsed);
                }
                else
                {
                    Log.Debug(
                        "Encountered error during NNTP Operation: `{Message}`. Trying another provider.",
                        failure.Summary);
                }

                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private List<MultiConnectionNntpClient> GetOrderedProviders()
    {
        var enabled = providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .ToList();

        var healthy = enabled.Where(x => !x.IsTripped).ToList();
        var candidates = healthy.Count > 0 ? healthy : enabled;

        return candidates
            .OrderBy(x => x.ProviderType)
            .ThenByDescending(x => performanceStore.GetSortScore(x.ProviderHost))
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();
    }

    private static string FormatSegmentId(string? segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId)) return "n/a";
        return segmentId.Length <= 16 ? segmentId : segmentId[^16..];
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
