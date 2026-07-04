using Microsoft.AspNetCore.Http;
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
        HttpContext httpContext)
    {
        var rangeHeader = httpContext.Request.Headers.Range.ToString();
        var scope = FileAccessTelemetry.BeginScope(
            fileName,
            fileSize,
            startOffset: 0,
            segmentCount,
            articleBufferSize,
            string.IsNullOrWhiteSpace(rangeHeader) ? null : rangeHeader);

        return new FileAccessLoggingStream(inner, scope);
    }
}
