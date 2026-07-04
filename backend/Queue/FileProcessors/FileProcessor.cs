using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.FileProcessors;

public class FileProcessor(
    GetFileInfosStep.FileInfo fileInfo,
    INntpClient usenetClient,
    CancellationToken ct
) : BaseProcessor
{
    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        try
        {
            var fileSize = fileInfo.FileSize ?? await usenetClient
                .GetFileSizeAsync(fileInfo.NzbFile, ct)
                .ConfigureAwait(false);

            long firstPartOffset = 0;
            var standardPartSize = 0;
            if (fileInfo.FirstSegmentHeader is not null &&
                SegmentSeekMap.TryCreate(
                    fileSize,
                    fileInfo.NzbFile.Segments.Count,
                    fileInfo.FirstSegmentHeader,
                    out firstPartOffset,
                    out standardPartSize))
            {
                // seek map stored below
            }
            else
            {
                firstPartOffset = 0;
                standardPartSize = 0;
            }

            return new Result()
            {
                NzbFile = fileInfo.NzbFile,
                FileName = fileInfo.FileName,
                FileSize = fileSize,
                ReleaseDate = fileInfo.ReleaseDate,
                FirstPartOffset = firstPartOffset,
                StandardPartSize = standardPartSize,
            };
        }

        // Ignore missing articles if it's not a video file.
        // In that case, simply skip the file altogether.
        catch (UsenetArticleNotFoundException) when (!FilenameUtil.IsVideoFile(fileInfo.FileName))
        {
            Log.Warning($"File `{fileInfo.FileName}` has missing articles. Skipping file since it is not a video.");
            return null;
        }
    }

    public new class Result : BaseProcessor.Result
    {
        public required NzbFile NzbFile { get; init; }
        public required string FileName { get; init; }
        public required long FileSize { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }
        public long FirstPartOffset { get; init; }
        public int StandardPartSize { get; init; }
    }
}