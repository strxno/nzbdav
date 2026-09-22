using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.DownloadAllNzbs;

[ApiController]
[Route("api/download-nzbs")]
public class DownloadAllNzbsController(DavDatabaseContext dbContext) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var nzbs = await GetExportableNzbs(ct).ConfigureAwait(false);
        if (nzbs.Count == 0)
            throw new BadHttpRequestException("No NZBs available to export.");

        var tempFile = Path.Combine(Path.GetTempPath(), $"nzbdav-nzbs-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var nzb in nzbs)
                {
                    ct.ThrowIfCancellationRequested();
                    await using var source = BlobStore.ReadBlob(nzb.Id);
                    if (source is null) continue;

                    var entry = archive.CreateEntry(
                        GetUniqueEntryName(usedNames, nzb.Category, nzb.FileName),
                        CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await source.CopyToAsync(entryStream, ct).ConfigureAwait(false);
                }
            }

            var readStream = new FileStream(
                tempFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return File(readStream, "application/octet-stream", $"nzbs-{DateTime.UtcNow:yyyy-MM-dd}.zip");
        }
        catch
        {
            try { System.IO.File.Delete(tempFile); }
            catch { /* ignore cleanup failures */ }
            throw;
        }
    }

    private async Task<List<NzbExportItem>> GetExportableNzbs(CancellationToken ct)
    {
        var nzbNames = await dbContext.NzbNames.AsNoTracking()
            .Select(x => new { x.Id, x.FileName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var queueCategories = await dbContext.QueueItems.AsNoTracking()
            .Select(x => new { x.Id, x.Category })
            .ToDictionaryAsync(x => x.Id, x => x.Category, ct)
            .ConfigureAwait(false);

        var historyCategories = await dbContext.HistoryItems.AsNoTracking()
            .Where(x => x.NzbBlobId != null)
            .Select(x => new { Id = x.NzbBlobId!.Value, x.Category })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var historyCategoryMap = new Dictionary<Guid, string>();
        foreach (var item in historyCategories)
            historyCategoryMap.TryAdd(item.Id, item.Category);

        var result = new List<NzbExportItem>(nzbNames.Count);
        foreach (var name in nzbNames)
        {
            if (!BlobStore.Exists(name.Id)) continue;

            var category = queueCategories.GetValueOrDefault(name.Id)
                ?? historyCategoryMap.GetValueOrDefault(name.Id)
                ?? "uncategorized";
            result.Add(new NzbExportItem(name.Id, name.FileName, category));
        }

        return result;
    }

    private static string GetUniqueEntryName(HashSet<string> usedNames, string category, string fileName)
    {
        var safeCategory = SanitizePathSegment(category);
        if (string.IsNullOrEmpty(safeCategory))
            safeCategory = "uncategorized";

        var safeFileName = SanitizePathSegment(fileName);
        if (string.IsNullOrEmpty(safeFileName))
            safeFileName = "unnamed.nzb";
        if (string.IsNullOrEmpty(Path.GetExtension(safeFileName)))
            safeFileName += ".nzb";

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

    private record NzbExportItem(Guid Id, string FileName, string Category);
}
