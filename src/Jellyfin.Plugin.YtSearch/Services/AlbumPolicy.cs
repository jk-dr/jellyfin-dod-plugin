namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Makes sure every track gets an artist and an album, so it is filed as Artist/Album/ like any other song in a library.</summary>
public static class AlbumPolicy
{
    /// <summary>
    /// The catalog match when there is one. Otherwise an album named after the song by the track's artist,
    /// so even songs with no known album get their own artist and album folders instead of sitting loose in the library.
    /// </summary>
    public static TrackMeta For(TrackResult r, TrackMeta? catalog)
    {
        if (catalog is not null)
        {
            return catalog;
        }

        var artist = InputGuard.CleanText(r.CleanArtist, 200, "Unknown Artist");
        var title = InputGuard.CleanText(StripArtistPrefix(r.Title, r.ArtistNames), 200, "Unknown");
        return new TrackMeta(title, artist, title, artist, null, null, null, null, null);
    }

    /// <summary>"Artist - Song" becomes "Song" when the part before the dash is one of the track's artists.</summary>
    internal static string StripArtistPrefix(string title, System.Collections.Generic.IEnumerable<string> artists)
    {
        foreach (var artist in artists)
        {
            foreach (var dash in new[] { " - ", " – ", " — " })
            {
                var prefix = artist + dash;
                if (title.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase) && title.Length > prefix.Length)
                {
                    return title[prefix.Length..].Trim();
                }
            }
        }

        return title;
    }
}
