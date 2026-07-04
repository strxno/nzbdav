using MemoryPack;

namespace NzbWebDAV.Database.Models;

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class DavNzbFile
{
    [MemoryPackOrder(0)]
    public Guid Id { get; set; } // foreign key to DavItem.Id

    [MemoryPackOrder(1)]
    public string[] SegmentIds { get; set; } = [];

    /// <summary>
    /// Decoded file byte offset of the first segment (=ypart begin), used for O(1) seek.
    /// </summary>
    [MemoryPackOrder(2)]
    public long FirstPartOffset { get; set; }

    /// <summary>
    /// Decoded size of each full segment except possibly the last. Zero means unknown (legacy files).
    /// </summary>
    [MemoryPackOrder(3)]
    public int StandardPartSize { get; set; }

    // navigation helpers
    [MemoryPackIgnore]
    public DavItem? DavItem { get; set; }
}