namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Makes sure every track gets an artist and an album, so it is filed as Artist/Album/ like any other song in a library.</summary>
public static class AlbumPolicy
{
    /// <summary>
    /// The catalog match when there is one. Otherwise a single named after the song ("Title - Single") by the track's artist,
    /// so even songs with no known album get their own artist and album folders instead of sitting loose in the library.
    /// </summary>
    public static TrackMeta For(TrackResult r, TrackMeta? catalog)
    {
        if (catalog is not null)
        {
            return catalog;
        }

        var title = InputGuard.CleanText(r.Title, 200, "Unknown");
        var artist = InputGuard.CleanText(r.CleanArtist, 200, "Unknown Artist");
        return new TrackMeta(title, artist, InputGuard.CleanText(title + " - Single", 200, "Singles"), artist, null, null, null, null, null);
    }
}
