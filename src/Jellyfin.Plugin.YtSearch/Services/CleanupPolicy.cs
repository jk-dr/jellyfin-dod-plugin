using System;

namespace Jellyfin.Plugin.YtSearch.Services;

public static class CleanupPolicy
{
    /// <summary>
    /// A track may be deleted when it was played at most <paramref name="maxPlays"/> times, is nobody's favorite,
    /// is in no playlist, and neither downloaded nor played for <paramref name="retentionDays"/> days.
    /// </summary>
    public static bool ShouldDelete(int totalPlays, bool isFavorite, bool inPlaylist, DateTime lastActivityUtc, DateTime nowUtc, int maxPlays, int retentionDays) =>
        totalPlays <= maxPlays
        && !isFavorite
        && !inPlaylist
        && nowUtc - lastActivityUtc >= TimeSpan.FromDays(retentionDays);
}
