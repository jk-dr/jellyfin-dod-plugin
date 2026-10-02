using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.YtSearch.Services;

public static class Sources
{
    public const string YouTube = "youtube";
    public const string SoundCloud = "soundcloud";
}

/// <summary>One search hit from any source. SourceId is the YouTube video id or SoundCloud track id.</summary>
public sealed record TrackResult(string Source, string SourceId, string Title, string Artist, double DurationSeconds, string ThumbnailUrl, string PageUrl)
{
    public long RunTimeTicks => (long)(DurationSeconds * TimeSpan.TicksPerSecond);

    private readonly Guid? _trackId;

    /// <summary>The id Jellyfin gives the item once it exists, set by <see cref="LibraryService"/>. Falls back to a stable hash.</summary>
    public Guid TrackId
    {
        get => _trackId ?? StableGuid($"{Source}-track:{SourceId}");
        init => _trackId = value;
    }

    public Guid AlbumId => StableGuid($"{Source}-album:{SourceId}");

    public Guid ArtistId => StableGuid($"{Source}-artist:{Artist}");

    /// <summary>Tag used in image tags so clients cache per track.</summary>
    public string ImageTag => (Source == Sources.YouTube ? "yt" : "sc") + SourceId;

    private static Guid StableGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
