using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Clients.Usenet.Telemetry;

public sealed class ProviderSpeedTestPersistence(ConfigManager configManager)
{
    public const string ConfigKey = UsenetProviderSpeedTestConfig.ConfigKey;

    public void LoadIntoStore(ProviderPerformanceStore store)
    {
        var config = configManager.GetProviderSpeedTestConfig();
        foreach (var result in config.Results)
        {
            store.SetSpeedTestResult(
                result.Host,
                result.MegabitsPerSecond,
                result.AverageTtfbMs,
                result.Success);
        }
    }

    public async Task SaveAsync(IReadOnlyList<ProviderSpeedTestResult> results, CancellationToken cancellationToken)
    {
        var config = new UsenetProviderSpeedTestConfig
        {
            TestedAt = DateTime.UtcNow,
            Results = results.Select(x => new ProviderSpeedTestEntry
            {
                Host = x.Host,
                ProviderIndex = x.ProviderIndex,
                Success = x.Success,
                Error = x.Error,
                MegabitsPerSecond = x.MegabitsPerSecond,
                AverageTtfbMs = x.AverageTtfbMs,
                InitialTtfbMs = x.InitialTtfbMs,
                DurationSeconds = x.DurationSeconds,
                SortRank = x.SortRank
            }).ToList()
        };

        var json = JsonSerializer.Serialize(config);

        await using var dbContext = new DavDatabaseContext();
        var existing = await dbContext.ConfigItems
            .FirstOrDefaultAsync(x => x.ConfigName == ConfigKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            dbContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = ConfigKey,
                ConfigValue = json
            });
        }
        else
        {
            existing.ConfigValue = json;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = ConfigKey,
                ConfigValue = json
            }
        ]);
    }
}
