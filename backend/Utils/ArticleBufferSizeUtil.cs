using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Utils;

public static class ArticleBufferSizeUtil
{
    /// <summary>
    /// Typical decoded payload per Usenet article (~750 KB). Used to cap prefetch when
    /// segment count is inflated by metadata or uneven multipart layouts.
    /// </summary>
    private const int TypicalSegmentBytes = 750_000;

    /// <summary>
    /// Caps article prefetch to roughly what the HTTP range needs instead of always
    /// using the configured global maximum (e.g. 200 segments for a small JPG range).
    /// </summary>
    public static int ForHttpRequest(
        HttpContext httpContext,
        long fileSize,
        int segmentCount,
        int configuredMax,
        int standardPartSize = 0)
    {
        if (configuredMax == 0 || segmentCount <= 0 || fileSize <= 0)
            return configuredMax;

        var rangeHeader = httpContext.Request.Headers.Range.ToString();
        if (string.IsNullOrWhiteSpace(rangeHeader))
            return configuredMax;

        if (!TryParseByteRange(rangeHeader, fileSize, out var rangeStart, out var rangeEnd))
            return configuredMax;

        return ForByteRange(rangeStart, rangeEnd, fileSize, segmentCount, configuredMax, standardPartSize);
    }

    public static int ForByteRange(
        long rangeStart,
        long rangeEnd,
        long fileSize,
        int segmentCount,
        int configuredMax,
        int standardPartSize = 0)
    {
        if (configuredMax == 0 || segmentCount <= 0 || fileSize <= 0)
            return configuredMax;

        var rangeBytes = rangeEnd - rangeStart + 1;
        if (rangeBytes <= 0)
            return Math.Min(configuredMax, segmentCount);

        var estimatedSegmentBytes = EstimateSegmentBytes(fileSize, segmentCount, standardPartSize);
        var segmentsInRange = (int)Math.Ceiling(rangeBytes / (double)estimatedSegmentBytes);

        var startSegmentIndex = (int)Math.Min(segmentCount, rangeStart / estimatedSegmentBytes);
        var segmentsFromOffset = segmentCount - startSegmentIndex;
        var effective = Math.Min(segmentsInRange + 2, segmentsFromOffset);

        return Math.Clamp(effective, 1, Math.Min(configuredMax, segmentCount));
    }

    private static long EstimateSegmentBytes(long fileSize, int segmentCount, int standardPartSize)
    {
        if (standardPartSize > 0)
            return standardPartSize;

        var averageBytes = fileSize / Math.Max(1, segmentCount);
        if (averageBytes >= TypicalSegmentBytes / 4)
            return Math.Max(1, averageBytes);

        return TypicalSegmentBytes;
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
