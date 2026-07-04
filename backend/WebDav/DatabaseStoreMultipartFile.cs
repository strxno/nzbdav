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

public class DatabaseStoreMultipartFile(
    DavItem davMultipartFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager
) : BaseStoreStreamFile(httpContext)
{
    public DavItem DavItem => davMultipartFile;
    public override string Name => davMultipartFile.Name;
    public override string UniqueKey => davMultipartFile.Id.ToString();
    public override long FileSize => davMultipartFile.FileSize!.Value;
    public override DateTime CreatedAt => davMultipartFile.CreatedAt;
    public override Guid? NzbBlobId => davMultipartFile.NzbBlobId;

    protected override async Task<Stream> GetStreamAsync(CancellationToken ct)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davMultipartFile;

        var id = davMultipartFile.Id;
        var multipartFile = await dbClient.GetDavMultipartFileAsync(davMultipartFile, ct).ConfigureAwait(false);
        if (multipartFile is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");

        var segmentCount = multipartFile.Metadata.FileParts.Sum(x => x.SegmentIds.Length);
        var articleBufferSize = configManager.GetArticleBufferSize();
        var stream = GetStream(multipartFile);

        return FileAccessStreamFactory.Wrap(
            stream,
            Name,
            FileSize,
            segmentCount,
            articleBufferSize,
            httpContext);
    }

    private Stream GetStream(DavMultipartFile multipartFile)
    {
        var packedStream = new DavMultipartFileStream(
            multipartFile.Metadata.FileParts,
            usenetClient,
            configManager.GetArticleBufferSize()
        );

        return multipartFile.Metadata.AesParams != null
            ? new AesDecoderStream(packedStream, multipartFile.Metadata.AesParams)
            : packedStream;
    }
}