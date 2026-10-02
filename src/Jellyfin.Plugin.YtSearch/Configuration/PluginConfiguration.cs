using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YtSearch.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Path to a yt-dlp binary. Empty = plugin downloads and manages its own copy.</summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>Optional fallback: path to a Netscape cookies.txt mounted into the container. An uploaded file takes priority.</summary>
    public string CookiesPath { get; set; } = string.Empty;

    /// <summary>yt-dlp release channel: nightly, master or stable.</summary>
    public string UpdateChannel { get; set; } = "nightly";

    public int UpdateIntervalHours { get; set; } = 12;

    /// <summary>Where downloaded tracks live (added as a Music library automatically). Empty = plugin data folder.</summary>
    public string LibraryPath { get; set; } = string.Empty;

    public int DownloadTimeoutSeconds { get; set; } = 300;

    public bool EnableCleanup { get; set; } = true;

    /// <summary>Tracks played at most this many times (all users combined) are eligible for cleanup.</summary>
    public int CleanupMaxPlays { get; set; } = 3;

    /// <summary>Days since download or last play before a track is eligible for cleanup.</summary>
    public int CleanupRetentionDays { get; set; } = 7;

    /// <summary>Max YouTube results per search.</summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>Hide SoundCloud results that can't be downloaded (DRM / preview-only).</summary>
    public bool HideUnplayableSoundCloud { get; set; } = true;

    public bool EnableSoundCloud { get; set; } = true;

    public int SoundCloudMaxResults { get; set; } = 10;

    public int SearchCacheMinutes { get; set; } = 5;

    public int SearchTimeoutSeconds { get; set; } = 8;

    public bool LogSearchRequests { get; set; } = true;
}
