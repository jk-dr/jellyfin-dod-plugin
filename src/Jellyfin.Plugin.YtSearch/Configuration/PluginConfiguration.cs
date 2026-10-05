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

    /// <summary>New downloads are refused once downloaded tracks use more than this many megabytes.</summary>
    public int MaxLibraryMegabytes { get; set; } = 20480;

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

    /// <summary>Look up the real album, track number, year and cover art of songs (Apple iTunes Search, no key).</summary>
    public bool LookUpAlbums { get; set; } = true;

    /// <summary>Measure each download's loudness and store it as ReplayGain tags, so players can level songs (no re-encoding).</summary>
    public bool NormalizeAudio { get; set; } = true;

    /// <summary>Fetch timed lyrics from lrclib.net (artist, title and length are sent there) and save them next to the song.</summary>
    public bool FetchLyrics { get; set; } = true;

    /// <summary>Fill artist pages with the songs and albums found on the artist's own YouTube channel and SoundCloud profile.</summary>
    public bool UseArtistProfiles { get; set; } = true;

    /// <summary>
    /// For apps that search a synced copy of the library instead of asking the server (e.g. Manet): results searched for in the
    /// last this many days are added to the library lists those apps sync. 0 turns it off.
    /// </summary>
    public int SyncRecentDays { get; set; } = 7;

    /// <summary>Apps (by name, comma separated) that get the recent results in their library sync.</summary>
    public string SyncClients { get; set; } = "Manet";

    public bool EnableSoundCloud { get; set; } = true;

    public int SoundCloudMaxResults { get; set; } = 10;

    public int SearchCacheMinutes { get; set; } = 5;

    public int SearchTimeoutSeconds { get; set; } = 8;

    public bool LogSearchRequests { get; set; } = true;

    /// <summary>Debug: log method, path (no query string) and client of every API request, to see what an app calls.</summary>
    public bool LogAllApiRequests { get; set; }
}
