using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.YtSearch.Services;

public static class Sources
{
    public const string YouTube = "youtube";
    public const string SoundCloud = "soundcloud";
}

/// <summary>Real music metadata for a track (from a catalog lookup). Without it a track is just a loose file.</summary>
public sealed record TrackMeta(string Title, string Artist, string Album, string AlbumArtist, int? Year, int? TrackNumber, int? DiscNumber, string? Genre, string? ArtworkUrl = null);

/// <summary>One search hit from any source. SourceId is the YouTube video id or SoundCloud track id.</summary>
public sealed record TrackResult(string Source, string SourceId, string Title, string Artist, double DurationSeconds, string ThumbnailUrl, string PageUrl)
{
    private readonly Guid? _trackId;
    private readonly Guid? _albumId;

    /// <summary>Catalog metadata when a confident match was found.</summary>
    public TrackMeta? Meta { get; init; }

    public long RunTimeTicks => (long)(DurationSeconds * TimeSpan.TicksPerSecond);

    /// <summary>The id Jellyfin gives the item once it exists, set by <see cref="LibraryService"/>. Falls back to a stable hash.</summary>
    public Guid TrackId
    {
        get => _trackId ?? StableGuid($"{Source}-track:{SourceId}");
        init => _trackId = value;
    }

    /// <summary>The id of the album Jellyfin creates for the track's folder (only meaningful when <see cref="Meta"/> is set).</summary>
    public Guid AlbumId
    {
        get => _albumId ?? StableGuid($"{Source}-album:{SourceId}");
        init => _albumId = value;
    }

    public Guid ArtistId => StableGuid($"{Source}-artist:{DisplayArtist}");

    public string DisplayTitle => Meta?.Title ?? Title;

    public string DisplayArtist => Meta?.Artist ?? Artist;

    public string DisplayAlbumArtist => Meta?.AlbumArtist ?? Artist;

    /// <summary>Tag used in image tags so clients cache per track.</summary>
    public string ImageTag => (Source == Sources.YouTube ? "yt" : "sc") + SourceId;

    public string Key => $"{Source}:{SourceId}";

    private static Guid StableGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
