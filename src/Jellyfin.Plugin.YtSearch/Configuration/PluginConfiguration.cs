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

    public int MaxResults { get; set; } = 10;

    public int SearchCacheMinutes { get; set; } = 5;

    public int SearchTimeoutSeconds { get; set; } = 8;

    public bool LogSearchRequests { get; set; } = true;
}
