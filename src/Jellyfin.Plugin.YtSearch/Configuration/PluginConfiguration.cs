using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YtSearch.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string YtDlpPath { get; set; } = "yt-dlp";

    public string LibraryPath { get; set; } = "/var/lib/jellyfin/youtube";

    public int MaxResults { get; set; } = 10;

    public int SearchCacheMinutes { get; set; } = 5;
}
