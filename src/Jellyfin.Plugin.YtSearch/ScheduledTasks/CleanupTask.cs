using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YtSearch.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.YtSearch.ScheduledTasks;

public class CleanupTask : IScheduledTask
{
    private readonly CleanupService _cleanup;

    public CleanupTask(CleanupService cleanup)
    {
        _cleanup = cleanup;
    }

    public string Name => "Clean up old YouTube/SoundCloud tracks";

    public string Key => "YtSearchCleanup";

    public string Description => "Deletes downloaded tracks that were played rarely and are not in favorites or playlists.";

    public string Category => "YouTube Search";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _cleanup.RunAsync().ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
    }
}
