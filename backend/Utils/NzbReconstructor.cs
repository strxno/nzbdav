using System.Globalization;
using System.Text;
using System.Xml;

namespace NzbWebDAV.Utils;

public static class NzbReconstructor
{
    private static readonly XmlWriterSettings XmlSettings = new()
    {
        Async = true,
        CloseOutput = false,
        Encoding = new UTF8Encoding(false),
        Indent = true,
        OmitXmlDeclaration = false
    };

    public static async Task WriteAsync(
        Stream stream,
        string title,
        string? category,
        IReadOnlyList<ReconstructedFile> files,
        CancellationToken ct)
    {
        await using var writer = XmlWriter.Create(stream, XmlSettings);
        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        await writer.WriteDocTypeAsync(
            "nzb",
            "-//newzBin//DTD NZB 1.1//EN",
            "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd",
            null
        ).ConfigureAwait(false);
        await writer.WriteStartElementAsync(null, "nzb", "http://www.newzbin.com/dtd/nzb/nzb-1.1.dtd")
            .ConfigureAwait(false);

        await writer.WriteStartElementAsync(null, "head", null).ConfigureAwait(false);
        await WriteMetaAsync(writer, "title", title).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(category))
            await WriteMetaAsync(writer, "category", category).ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.SegmentIds.Length == 0) continue;
            await WriteFileAsync(writer, file).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteMetaAsync(XmlWriter writer, string type, string value)
    {
        await writer.WriteStartElementAsync(null, "meta", null).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "type", null, type).ConfigureAwait(false);
        await writer.WriteStringAsync(value).ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteFileAsync(XmlWriter writer, ReconstructedFile file)
    {
        var date = file.Date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var subject = $"[1/1] \"{file.FileName}\" yEnc (1/{file.SegmentIds.Length}) {file.TotalBytes}";

        await writer.WriteStartElementAsync(null, "file", null).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "poster", null, "nzbdav@nzbdav").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "date", null, date).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "subject", null, subject).ConfigureAwait(false);

        await writer.WriteStartElementAsync(null, "groups", null).ConfigureAwait(false);
        await writer.WriteElementStringAsync(null, "group", null, "alt.binaries.misc").ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);

        await writer.WriteStartElementAsync(null, "segments", null).ConfigureAwait(false);
        for (var i = 0; i < file.SegmentIds.Length; i++)
        {
            var bytes = GetSegmentBytes(file.TotalBytes, i, file.SegmentIds.Length);
            await writer.WriteStartElementAsync(null, "segment", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "bytes", null, bytes.ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "number", null, (i + 1).ToString(CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
            await writer.WriteStringAsync(file.SegmentIds[i].Trim('<', '>')).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static long GetSegmentBytes(long totalBytes, int index, int count)
    {
        if (count <= 0 || totalBytes <= 0) return 0;
        var baseSize = totalBytes / count;
        return index == count - 1 ? Math.Max(0, totalBytes - baseSize * (count - 1)) : baseSize;
    }

    public static IReadOnlyList<ReconstructedFile> Deduplicate(IEnumerable<ReconstructedFile> files)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReconstructedFile>();
        foreach (var file in files)
        {
            if (file.SegmentIds.Length == 0) continue;
            var key = string.Join('\n', file.SegmentIds);
            if (!seen.Add(key)) continue;
            result.Add(file);
        }

        return result;
    }

    public readonly record struct ReconstructedFile(
        string FileName,
        string[] SegmentIds,
        long TotalBytes,
        DateTimeOffset Date
    );
}
