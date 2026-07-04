using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Telemetry;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreNzbFile(
    DavItem davNzbFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager
) : BaseStoreStreamFile(httpContext)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SeekMapLocks = new();

    public DavItem DavItem => davNzbFile;
    public override string Name => davNzbFile.Name;
    public override string UniqueKey => davNzbFile.Id.ToString();
    public override long FileSize => davNzbFile.FileSize!.Value;
    public override DateTime CreatedAt => davNzbFile.CreatedAt;
    public override Guid? NzbBlobId => davNzbFile.NzbBlobId;

    protected override async Task<Stream> GetStreamAsync(CancellationToken cancellationToken)
    {
        httpContext.Items["DavItem"] = davNzbFile;

        var id = davNzbFile.Id;
        var file = await dbClient.GetDavNzbFileAsync(davNzbFile, cancellationToken).ConfigureAwait(false);
        if (file is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");

        var articleBufferSize = configManager.GetArticleBufferSize();

        var stream = usenetClient.GetFileStream(
            file.SegmentIds,
            FileSize,
            articleBufferSize,
            file.FirstPartOffset,
            file.StandardPartSize,
            ct => ResolveSeekMapAsync(file, ct));

        return FileAccessStreamFactory.Wrap(
            stream,
            Name,
            FileSize,
            file.SegmentIds.Length,
            articleBufferSize,
            httpContext);
    }

    private async Task<(long FirstPartOffset, int StandardPartSize)> ResolveSeekMapAsync
    (
        DavNzbFile nzbFile,
        CancellationToken cancellationToken
    )
    {
        if (nzbFile.StandardPartSize > 0)
            return (nzbFile.FirstPartOffset, nzbFile.StandardPartSize);

        if (nzbFile.SegmentIds.Length == 0)
            return (0, 0);

        var semaphore = SeekMapLocks.GetOrAdd(nzbFile.Id, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (nzbFile.StandardPartSize > 0)
                return (nzbFile.FirstPartOffset, nzbFile.StandardPartSize);

            var header = await usenetClient
                .GetYencHeadersAsync(nzbFile.SegmentIds[0], cancellationToken)
                .ConfigureAwait(false);

            if (!SegmentSeekMap.TryCreate(
                    FileSize,
                    nzbFile.SegmentIds.Length,
                    header,
                    out var firstPartOffset,
                    out var standardPartSize))
            {
                return (0, 0);
            }

            nzbFile.FirstPartOffset = firstPartOffset;
            nzbFile.StandardPartSize = standardPartSize;
            await dbClient.PersistDavNzbFileAsync(nzbFile, davNzbFile, cancellationToken).ConfigureAwait(false);
            return (firstPartOffset, standardPartSize);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
