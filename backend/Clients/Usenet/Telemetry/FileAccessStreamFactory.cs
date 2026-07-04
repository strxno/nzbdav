using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

public static class FileAccessStreamFactory
{
    public static Stream Wrap(
        Stream inner,
        string fileName,
        long fileSize,
        int segmentCount,
        int articleBufferSize,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsHead(httpContext.Request.Method))
            return inner;

        var rangeHeader = httpContext.Request.Headers.Range.ToString();
        var rangeStart = ArticleBufferSizeUtil.TryParseByteRange(rangeHeader, fileSize, out var start, out _)
            ? start
            : 0;
        var effectiveBuffer = ArticleBufferSizeUtil.ForHttpRequest(
            httpContext, fileSize, segmentCount, articleBufferSize);

        var session = new FileAccessSession(
            fileName,
            fileSize,
            rangeStart,
            segmentCount,
            effectiveBuffer,
            string.IsNullOrWhiteSpace(rangeHeader) ? null : rangeHeader,
            articleBufferSize);

        var holder = new FileAccessSessionHolder(session);
        var tokenContext = cancellationToken.SetContext(holder);
        httpContext.Response.OnCompleted(() =>
        {
            tokenContext.Dispose();
            return Task.CompletedTask;
        });

        return new FileAccessLoggingStream(inner, new FileAccessSessionScope(session));
    }
}
