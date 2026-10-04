using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>Each artist of the credit separately ("A, B &amp; C" gives three), band names like "Simon &amp; Garfunkel" kept whole.</summary>
    public IReadOnlyList<string> ArtistNames => ArtistSplitter.Split(DisplayArtist);

    /// <summary>The first album artist: the one folders and the album are filed under.</summary>
    public string PrimaryAlbumArtist => ArtistSplitter.Split(DisplayAlbumArtist)[0];

    /// <summary>The id of the first (main) artist.</summary>
    public Guid ArtistId => ArtistIdFor(ArtistNames[0]);

    public Guid ArtistIdFor(string name) => StableGuid($"{Source}-artist:{name}");

    /// <summary>The artist of this track (or its album) that has the given id, if any.</summary>
    public string? ArtistNameFor(Guid id) => ArtistNames.Concat(ArtistSplitter.Split(DisplayAlbumArtist)).FirstOrDefault(n => ArtistIdFor(n) == id);

    public string DisplayTitle => Meta?.Title ?? Title;

    /// <summary>The channel name without YouTube's automatic " - Topic" suffix.</summary>
    public string CleanArtist => StripTopic(Artist);

    public string DisplayArtist => Meta?.Artist ?? CleanArtist;

    public string DisplayAlbumArtist => Meta?.AlbumArtist ?? CleanArtist;

    /// <summary>Tag used in image tags so clients cache per track.</summary>
    public string ImageTag => (Source == Sources.YouTube ? "yt" : "sc") + SourceId;

    public string Key => $"{Source}:{SourceId}";

    private static readonly Regex TopicSuffix = new(@"\s*[-–—]\s*Topic\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string StripTopic(string? artist)
    {
        var stripped = TopicSuffix.Replace(artist ?? string.Empty, string.Empty).Trim();
        return stripped.Length > 0 ? stripped : artist ?? string.Empty;
    }

    private static Guid StableGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
