using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YtSearch.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Path to a yt-dlp binary. Empty = plugin downloads and manages its own copy.</summary>
    public string YtDlpPath { get; set; } = string.Empty;

    public int MaxResults { get; set; } = 10;

    public int SearchCacheMinutes { get; set; } = 5;

    public int SearchTimeoutSeconds { get; set; } = 8;

    public bool LogSearchRequests { get; set; } = true;
}
