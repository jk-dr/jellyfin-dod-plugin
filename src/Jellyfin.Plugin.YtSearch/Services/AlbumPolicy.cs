namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Applies the user's "where do downloads go" setting to a track's album metadata.</summary>
public static class AlbumPolicy
{
    /// <summary>
    /// In "fixed" mode every track goes into one album the user named (artist and title stay per track);
    /// otherwise the catalog match (or none) is kept as is.
    /// </summary>
    public static TrackMeta? Apply(string? mode, string? fixedAlbum, string? fixedAlbumArtist, TrackResult r, TrackMeta? catalog)
    {
        if (mode != "fixed")
        {
            return catalog;
        }

        var album = InputGuard.CleanText(fixedAlbum, 200, "YouTube & SoundCloud");
        var albumArtist = InputGuard.CleanText(fixedAlbumArtist, 200, "Various Artists");
        var basis = catalog ?? new TrackMeta(r.Title, r.Artist, album, albumArtist, null, null, null, null, null);
        return basis with { Album = album, AlbumArtist = albumArtist, TrackNumber = null, DiscNumber = null };
    }

    /// <summary>Meta for a track the user places into a chosen album.</summary>
    public static TrackMeta Place(TrackResult r, string album, string albumArtist)
    {
        var basis = r.Meta ?? new TrackMeta(r.Title, r.Artist, album, albumArtist, null, null, null, null, null);
        return basis with { Album = InputGuard.CleanText(album, 200, "Unknown"), AlbumArtist = InputGuard.CleanText(albumArtist, 200, "Unknown"), TrackNumber = null, DiscNumber = null };
    }
}
