using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.DownloadAllNzbs;

[ApiController]
[Route("api/download-nzbs")]
public class DownloadAllNzbsController(DavDatabaseContext dbContext) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var historyItemIdStr = HttpContext.Request.Query["historyItemId"].FirstOrDefault();
        if (Guid.TryParse(historyItemIdStr, out var historyItemId))
            return await ExportSingleAsync(historyItemId, ct).ConfigureAwait(false);

        return await ExportAllAsync(ct).ConfigureAwait(false);
    }

    private async Task<IActionResult> ExportSingleAsync(Guid historyItemId, CancellationToken ct)
    {
        var groups = await GetExportGroups(ct).ConfigureAwait(false);
        var group = groups.FirstOrDefault(x => x.HistoryItemId == historyItemId);
        if (group is null)
            throw new BadHttpRequestException("No downloaded files found for this NZB.");

        var files = await BuildReconstructedFiles(group, ct).ConfigureAwait(false);
        if (files.Count == 0)
            throw new BadHttpRequestException("No downloaded files found for this NZB.");

        var stream = new MemoryStream();
        await NzbReconstructor.WriteAsync(stream, GetTitle(group.FileName), group.Category, files, ct)
            .ConfigureAwait(false);
        stream.Position = 0;
        return File(stream, "application/x-nzb", EnsureNzbExtension(group.FileName));
    }

    private async Task<IActionResult> ExportAllAsync(CancellationToken ct)
    {
        var groups = await GetExportGroups(ct).ConfigureAwait(false);
        if (groups.Count == 0)
            throw new BadHttpRequestException("No downloaded NZBs found to export.");

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "application/zip";
        Response.Headers["Content-Encoding"] = "identity";
        Response.Headers["Content-Disposition"] = GetContentDisposition(
            $"nzbs-{DateTime.UtcNow:yyyy-MM-dd}.zip");

        await Response.StartAsync(ct).ConfigureAwait(false);

        using var archive = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var files = await BuildReconstructedFiles(group, ct).ConfigureAwait(false);
            if (files.Count == 0) continue;

            var entry = archive.CreateEntry(
                GetUniqueEntryName(usedNames, group.Category, group.FileName),
                CompressionLevel.Fastest);
            await using var entryStream = entry.Open();
            await NzbReconstructor.WriteAsync(entryStream, GetTitle(group.FileName), group.Category, files, ct)
                .ConfigureAwait(false);
        }

        return new EmptyResult();
    }

    private async Task<List<ExportGroup>> GetExportGroups(CancellationToken ct)
    {
        var historyItems = await dbContext.HistoryItems.AsNoTracking()
            .Select(x => new { x.Id, x.FileName, x.Category })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var historyMap = historyItems.ToDictionary(x => x.Id);

        var davFiles = await dbContext.Items.AsNoTracking()
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .Select(x => new DavFileRef(
                x.Id,
                x.Name,
                x.FileSize,
                x.SubType,
                x.FileBlobId,
                x.HistoryItemId,
                x.Path,
                x.CreatedAt,
                x.ReleaseDate))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var groups = new Dictionary<string, ExportGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in davFiles)
        {
            string key;
            string category;
            string fileName;
            Guid? historyItemId = null;

            if (file.HistoryItemId is { } hid && historyMap.TryGetValue(hid, out var history))
            {
                key = hid.ToString();
                category = history.Category;
                fileName = history.FileName;
                historyItemId = hid;
            }
            else if (TryGetContentFolder(file.Path, out var contentCategory, out var jobName))
            {
                key = $"content:{contentCategory}/{jobName}";
                category = contentCategory;
                fileName = jobName;
            }
            else
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var group))
            {
                group = new ExportGroup(historyItemId, category, fileName, []);
                groups[key] = group;
            }

            group.Files.Add(file);
        }

        return groups.Values.ToList();
    }

    private async Task<IReadOnlyList<NzbReconstructor.ReconstructedFile>> BuildReconstructedFiles(
        ExportGroup group,
        CancellationToken ct)
    {
        var dbClient = new DavDatabaseClient(dbContext);
        var reconstructed = new List<NzbReconstructor.ReconstructedFile>();

        foreach (var file in group.Files.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var davItem = new DavItem
            {
                Id = file.Id,
                Name = file.Name,
                FileSize = file.FileSize,
                Type = DavItem.ItemType.UsenetFile,
                SubType = file.SubType,
                Path = file.Path,
                FileBlobId = file.FileBlobId
            };

            var date = file.ReleaseDate ?? new DateTimeOffset(file.CreatedAt);
            foreach (var part in await GetFileParts(davItem, dbClient, date, ct).ConfigureAwait(false))
                reconstructed.Add(part);
        }

        return NzbReconstructor.Deduplicate(reconstructed);
    }

    private static async Task<List<NzbReconstructor.ReconstructedFile>> GetFileParts(
        DavItem davItem,
        DavDatabaseClient dbClient,
        DateTimeOffset date,
        CancellationToken ct)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile)
        {
            var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
            if (nzbFile?.SegmentIds is not { Length: > 0 }) return [];
            return
            [
                new NzbReconstructor.ReconstructedFile(
                    davItem.Name,
                    nzbFile.SegmentIds,
                    davItem.FileSize ?? 0,
                    date)
            ];
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
            return FromParts(
                davItem.Name,
                date,
                rarFile?.RarParts?.Select(x => (x.SegmentIds, x.PartSize)).ToList()
            );
        }

        if (davItem.SubType == DavItem.ItemSubType.MultipartFile)
        {
            var multipartFile = await dbClient.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            return FromParts(
                davItem.Name,
                date,
                multipartFile?.Metadata?.FileParts?
                    .Select(x => (x.SegmentIds, x.SegmentIdByteRange.Count))
                    .ToList()
            );
        }

        return [];
    }

    private static List<NzbReconstructor.ReconstructedFile> FromParts(
        string fileName,
        DateTimeOffset date,
        List<(string[] SegmentIds, long TotalBytes)>? parts)
    {
        if (parts is not { Count: > 0 }) return [];

        var uniqueParts = parts
            .Where(x => x.SegmentIds is { Length: > 0 })
            .GroupBy(x => string.Join('\n', x.SegmentIds), StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();

        if (uniqueParts.Count == 1)
        {
            var part = uniqueParts[0];
            return [new NzbReconstructor.ReconstructedFile(fileName, part.SegmentIds, part.TotalBytes, date)];
        }

        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        return uniqueParts
            .Select((part, index) => new NzbReconstructor.ReconstructedFile(
                $"{baseName}.part{index + 1:000}{ext}",
                part.SegmentIds,
                part.TotalBytes,
                date))
            .ToList();
    }

    private static bool TryGetContentFolder(string path, out string category, out string jobName)
    {
        category = "";
        jobName = "";
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("content", StringComparison.OrdinalIgnoreCase))
            return false;

        category = parts[1];
        jobName = parts[2];
        return true;
    }

    private static string GetTitle(string fileName)
    {
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string EnsureNzbExtension(string fileName)
    {
        var safe = SanitizePathSegment(fileName);
        if (string.IsNullOrEmpty(safe)) safe = "unnamed.nzb";
        return string.IsNullOrEmpty(Path.GetExtension(safe)) ? safe + ".nzb" : safe;
    }

    private static string GetUniqueEntryName(HashSet<string> usedNames, string category, string fileName)
    {
        var safeCategory = SanitizePathSegment(category);
        if (string.IsNullOrEmpty(safeCategory))
            safeCategory = "uncategorized";

        var safeFileName = EnsureNzbExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(safeFileName);
        var ext = Path.GetExtension(safeFileName);
        var relative = $"{safeCategory}/{safeFileName}";
        var counter = 2;
        while (!usedNames.Add(relative))
        {
            relative = $"{safeCategory}/{baseName} ({counter}){ext}";
            counter++;
        }

        return relative;
    }

    private static string SanitizePathSegment(string value)
    {
        value = Path.GetFileName(value.Replace('\\', '/').Trim());
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        value = value.Trim().Trim('.');
        return value is "." or ".." ? "" : value;
    }

    private static string GetContentDisposition(string filename)
    {
        filename = new string(filename.Where(c => !char.IsControl(c)).ToArray());
        var chars = filename.Select(c => (c >= 32 && c <= 126 && c != '"' && c != '\\' && c != ';') ? c : '_');
        var ascii = new string(chars.ToArray());
        var utf8 = Uri.EscapeDataString(filename);
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{utf8}";
    }

    private sealed record DavFileRef(
        Guid Id,
        string Name,
        long? FileSize,
        DavItem.ItemSubType SubType,
        Guid? FileBlobId,
        Guid? HistoryItemId,
        string Path,
        DateTime CreatedAt,
        DateTimeOffset? ReleaseDate);

    private sealed record ExportGroup(
        Guid? HistoryItemId,
        string Category,
        string FileName,
        List<DavFileRef> Files);
}
