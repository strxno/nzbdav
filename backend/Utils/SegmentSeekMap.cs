using NzbWebDAV.Exceptions;
using UsenetSharp.Models;

namespace NzbWebDAV.Utils;

/// <summary>
/// Maps file byte offsets to Usenet segment indices using uniform segment sizes.
/// Usenet posts typically use fixed decoded part sizes; the last segment may be shorter.
/// </summary>
public static class SegmentSeekMap
{
    public static bool TryCreate
    (
        long fileSize,
        int segmentCount,
        UsenetYencHeader firstSegmentHeader,
        out long firstPartOffset,
        out int standardPartSize
    )
    {
        firstPartOffset = firstSegmentHeader.PartOffset;
        standardPartSize = (int)firstSegmentHeader.PartSize;

        if (segmentCount <= 0 || standardPartSize <= 0 || fileSize <= 0)
            return false;

        if (segmentCount == 1)
            return fileSize >= firstPartOffset && fileSize <= firstPartOffset + standardPartSize;

        var decodedSizeBeforeLast = firstPartOffset + (long)standardPartSize * (segmentCount - 1);
        var lastPartSize = fileSize - decodedSizeBeforeLast;

        return lastPartSize > 0 && lastPartSize <= standardPartSize;
    }

    public static int FindSegmentIndex
    (
        long byteOffset,
        long fileSize,
        int segmentCount,
        long firstPartOffset,
        int standardPartSize
    )
    {
        if (byteOffset < firstPartOffset || byteOffset >= fileSize)
            throw new SeekPositionNotFoundException($"Corrupt file. Cannot find byte position {byteOffset}.");

        var relativeOffset = byteOffset - firstPartOffset;
        var index = (int)(relativeOffset / standardPartSize);
        if (index >= segmentCount)
            index = segmentCount - 1;

        var segmentStart = firstPartOffset + (long)index * standardPartSize;
        var segmentEnd = index == segmentCount - 1
            ? fileSize
            : segmentStart + standardPartSize;

        if (byteOffset >= segmentEnd && index < segmentCount - 1)
            index++;

        return index;
    }

    public static long GetSegmentStartOffset(int segmentIndex, long firstPartOffset, int standardPartSize) =>
        firstPartOffset + (long)segmentIndex * standardPartSize;
}
