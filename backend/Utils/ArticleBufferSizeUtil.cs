using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Utils;

public static class ArticleBufferSizeUtil
{
    /// <summary>
    /// Caps article prefetch to roughly what the HTTP request needs instead of always
    /// using the configured global maximum (e.g. 200 segments for a 16 MB range probe).
    /// </summary>
    public static int ForHttpRequest(HttpContext httpContext, long fileSize, int segmentCount, int configuredMax)
    {
        if (configuredMax == 0 || segmentCount <= 0 || fileSize <= 0)
            return configuredMax;

        var rangeHeader = httpContext.Request.Headers.Range.ToString();
        if (string.IsNullOrWhiteSpace(rangeHeader))
            return configuredMax;

        if (!TryParseByteRange(rangeHeader, fileSize, out var rangeStart, out var rangeEnd))
            return configuredMax;

        var rangeBytes = rangeEnd - rangeStart + 1;
        if (rangeBytes <= 0)
            return Math.Min(configuredMax, segmentCount);

        var avgSegmentBytes = Math.Max(1, fileSize / segmentCount);
        var segmentsInRange = (int)Math.Ceiling(rangeBytes / (double)avgSegmentBytes);

        // Small slack for yEnc overhead and uneven segment sizes.
        var segmentsFromOffset = segmentCount - (int)Math.Min(segmentCount, rangeStart / avgSegmentBytes);
        var effective = Math.Min(segmentsInRange + 2, segmentsFromOffset);

        return Math.Clamp(effective, 1, Math.Min(configuredMax, segmentCount));
    }

    public static bool TryParseByteRange(string rangeHeader, long fileSize, out long start, out long end)
    {
        start = 0;
        end = fileSize - 1;

        if (string.IsNullOrWhiteSpace(rangeHeader)) return false;

        var equalsIndex = rangeHeader.IndexOf('=');
        if (equalsIndex < 0) return false;

        var spec = rangeHeader[(equalsIndex + 1)..].Trim();
        var dashIndex = spec.IndexOf('-');
        if (dashIndex < 0) return false;

        var startPart = spec[..dashIndex].Trim();
        var endPart = spec[(dashIndex + 1)..].Trim();

        if (startPart.Length == 0)
        {
            if (!long.TryParse(endPart, out var suffixLength)) return false;
            start = Math.Max(0, fileSize - suffixLength);
            end = fileSize - 1;
            return true;
        }

        if (!long.TryParse(startPart, out start)) return false;

        if (endPart.Length == 0)
            end = fileSize - 1;
        else if (!long.TryParse(endPart, out end))
            return false;

        end = Math.Min(end, fileSize - 1);
        start = Math.Clamp(start, 0, end);
        return true;
    }
}
