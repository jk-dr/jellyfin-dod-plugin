using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Keeps yt-dlp on the nightly channel: updates at startup, then periodically.</summary>
public class YtDlpUpdater : BackgroundService
{
    private readonly YtDlpService _ytdlp;
    private readonly ILogger<YtDlpUpdater> _logger;

    public YtDlpUpdater(YtDlpService ytdlp, ILogger<YtDlpUpdater> logger)
    {
        _ytdlp = ytdlp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _ytdlp.UpdateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "yt-dlp update check failed");
            }

            var hours = Math.Max(Plugin.Instance?.Configuration.UpdateIntervalHours ?? 12, 1);
            await Task.Delay(TimeSpan.FromHours(hours), stoppingToken).ConfigureAwait(false);
        }
    }
}
