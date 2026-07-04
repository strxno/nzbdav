using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Usenet;

namespace NzbWebDAV.Api.Controllers.TestUsenetSpeed;

[ApiController]
[Route("api/test-usenet-speed")]
public class TestUsenetSpeedController(UsenetProviderSpeedTestService speedTestService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (speedTestService.IsRunning)
        {
            return Conflict(new BaseApiResponse
            {
                Status = false,
                Error = "A provider speed test is already running."
            });
        }

        var results = await speedTestService
            .RunAllAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return Ok(new TestUsenetSpeedResponse
        {
            Status = true,
            Results = results.Select(x => new ProviderSpeedTestResultDto
            {
                ProviderIndex = x.ProviderIndex,
                Host = x.Host,
                Success = x.Success,
                Error = x.Error,
                BytesDownloaded = x.BytesDownloaded,
                DurationSeconds = x.DurationSeconds,
                MegabitsPerSecond = x.MegabitsPerSecond,
                AverageTtfbMs = x.AverageTtfbMs,
                InitialTtfbMs = x.InitialTtfbMs,
                SortRank = x.SortRank
            }).ToList()
        });
    }
}
