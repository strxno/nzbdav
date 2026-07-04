using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;

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
        var rangeStart = ParseRangeStart(rangeHeader);
        var session = new FileAccessSession(
            fileName,
            fileSize,
            rangeStart,
            segmentCount,
            articleBufferSize,
            string.IsNullOrWhiteSpace(rangeHeader) ? null : rangeHeader);

        var holder = new FileAccessSessionHolder(session);
        var tokenContext = cancellationToken.SetContext(holder);
        httpContext.Response.OnCompleted(() =>
        {
            tokenContext.Dispose();
            return Task.CompletedTask;
        });

        return new FileAccessLoggingStream(inner, new FileAccessSessionScope(session));
    }

    private static long ParseRangeStart(string rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader)) return 0;

        var equalsIndex = rangeHeader.IndexOf('=');
        if (equalsIndex < 0) return 0;

        var dashIndex = rangeHeader.IndexOf('-', equalsIndex + 1);
        if (dashIndex < 0) return 0;

        var startPart = rangeHeader[(equalsIndex + 1)..dashIndex].Trim();
        return long.TryParse(startPart, out var start) ? start : 0;
    }
}
