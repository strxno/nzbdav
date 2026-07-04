using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Telemetry;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreRarFile(
    DavItem davRarFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager
) : BaseStoreStreamFile(httpContext)
{
    public DavItem DavItem => davRarFile;
    public override string Name => davRarFile.Name;
    public override string UniqueKey => davRarFile.Id.ToString();
    public override long FileSize => davRarFile.FileSize!.Value;
    public override DateTime CreatedAt => davRarFile.CreatedAt;
    public override Guid? NzbBlobId => davRarFile.NzbBlobId;

    protected override async Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davRarFile;

        var id = davRarFile.Id;
        var rarFile = await dbClient.GetDavRarFileAsync(davRarFile, ct).ConfigureAwait(false);
        if (rarFile is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");

        var meta = rarFile.ToDavMultipartFileMeta();
        var segmentCount = meta.FileParts.Sum(x => x.SegmentIds.Length);
        var articleBufferSize = configManager.GetArticleBufferSize();
        var stream = GetStream(rarFile);

        return FileAccessStreamFactory.Wrap(
            stream,
            Name,
            FileSize,
            segmentCount,
            articleBufferSize,
            httpContext,
            ct);
    }

    private DavMultipartFileStream GetStream(DavRarFile rarFile)
    {
        return new DavMultipartFileStream
        (
            rarFile.ToDavMultipartFileMeta().FileParts,
            usenetClient,
            configManager.GetArticleBufferSize()
        );
    }
}