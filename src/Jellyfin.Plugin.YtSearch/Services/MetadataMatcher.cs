using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>One song from the catalog lookup.</summary>
public sealed record CatalogSong(string Title, string Artist, string Album, string AlbumArtist, int? Year, int? TrackNumber, int? DiscNumber, string? Genre, double DurationSeconds, string? ArtworkUrl, long TrackId = 0, long CollectionId = 0);

/// <summary>
/// Decides which online result is which catalog song. Deliberately strict: a wrong album is worse than no album,
/// so remixes, covers, live versions and long compilations never match.
/// </summary>
public static class MetadataMatcher
{
    private static readonly Regex Brackets = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex DashSuffix = new(@"\s+-\s+(remaster|remastered|single version|album version|radio edit|mono|stereo|version|\d{4}).*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NotAnAlbum = new(@"greatest hits|best of|the very best|karaoke|tribute|now that|compilation|workout|essential|ultimate|collection|#1|number ones|party|playlist|\bhits\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SpecialEdition = new(@"deluxe|anniversary|edition|remaster|expanded|bonus|special|collector|version|drumless|instrumental|acoustic|demo|live", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtistSplit = new(@"\s*(,|&|\band\b|\bfeat\.?\b|\bft\.?\b|\bwith\b|\bx\b)\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "official", "audio", "video", "lyrics", "lyric", "hd", "hq", "4k", "remaster", "remastered", "ft", "feat", "featuring", "music",
        "visualizer", "explicit", "topic", "vevo", "version", "full", "song", "original", "new", "with", "and", "the", "a", "of", "by",
        "mv", "m", "v", "clip", "single", "edit", "radio", "album", "from", "version",
    };

    public static bool IsFillerWord(string word) => Filler.Contains(word);

    /// <summary>
    /// Returns metadata keyed by <see cref="TrackResult.Key"/> for results that confidently match a catalog song.
    /// Each song goes to one upload only (the artist's own channel first), so an album never lists the same song several times.
    /// </summary>
    public static Dictionary<string, TrackMeta> Match(IReadOnlyList<TrackResult> ranked, IReadOnlyList<CatalogSong> songs)
    {
        var picks = new List<(TrackResult Result, int Rank, CatalogSong Song)>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var r = ranked[i];
            var best = songs
                .Where(s => Accepts(r, s))
                .OrderBy(s => IsCompilation(s) ? 1 : 0)
                .ThenBy(s => AlbumByOtherArtist(s) ? 1 : 0)
                .ThenBy(s => IsSpecialEdition(s) ? 1 : 0)
                .ThenBy(s => ReleaseKind(s))
                .ThenBy(s => s.Year ?? 9999)
                .ThenBy(s => Math.Abs(s.DurationSeconds - r.DurationSeconds))
                .FirstOrDefault();
            if (best is not null)
            {
                picks.Add((r, i, best));
            }
        }

        var result = new Dictionary<string, TrackMeta>();
        foreach (var group in picks.GroupBy(p => SongKey(p.Song)))
        {
            var winner = group
                .OrderByDescending(p => IsFromArtist(p.Result, p.Song) ? 1 : 0)
                .ThenBy(p => ExtraWords(p.Result, p.Song))
                .ThenBy(p => Math.Abs(p.Song.DurationSeconds - p.Result.DurationSeconds))
                .ThenBy(p => p.Rank)
                .First();
            var s = winner.Song;
            result[winner.Result.Key] = new TrackMeta(s.Title, s.Artist, s.Album, s.AlbumArtist, s.Year, s.TrackNumber, s.DiscNumber, s.Genre, s.ArtworkUrl);
        }

        return result;
    }

    internal static bool Accepts(TrackResult r, CatalogSong s)
    {
        var songTitle = RelevanceRanker.Tokens(CleanTitle(s.Title));
        if (songTitle.Count == 0)
        {
            return false;
        }

        var resultTitle = RelevanceRanker.Tokens(r.Title).ToHashSet();
        var resultArtist = RelevanceRanker.Tokens(r.Artist);
        var songTitleSet = songTitle.ToHashSet();
        if (!songTitleSet.IsSubsetOf(resultTitle))
        {
            return false;
        }

        var artistTokens = PrimaryArtistTokens(s);
        if (artistTokens.Count == 0 || !artistTokens.IsSubsetOf(resultTitle.Union(resultArtist)))
        {
            return false;
        }

        // A different version of the song: remix, cover, live, slowed...
        var fullTitle = RelevanceRanker.Tokens(s.Title).ToHashSet();
        if (resultTitle.Any(w => RelevanceRanker.IsNoiseWord(w) && !fullTitle.Contains(w)))
        {
            return false;
        }

        // Too many unexplained words usually means a compilation or a different song containing this title.
        if (ExtraWords(r, s) > 3)
        {
            return false;
        }

        // Radio edits and extended mixes differ a lot in length, so length is only a sanity check (no 10-hour loops, no snippets).
        if (s.DurationSeconds > 0 && r.DurationSeconds > 0)
        {
            var ratio = r.DurationSeconds / s.DurationSeconds;
            return ratio is >= 0.5 and <= 2.0;
        }

        return true;
    }

    /// <summary>Words in the upload's title that the song's own title, artist or common filler ("official audio") don't explain.</summary>
    private static int ExtraWords(TrackResult r, CatalogSong s)
    {
        var songTitle = RelevanceRanker.Tokens(CleanTitle(s.Title)).ToHashSet();
        var fullTitle = RelevanceRanker.Tokens(s.Title).ToHashSet();
        var artist = PrimaryArtistTokens(s);
        return RelevanceRanker.Tokens(r.Title).Distinct().Count(w => !songTitle.Contains(w) && !artist.Contains(w) && !Filler.Contains(w) && !fullTitle.Contains(w));
    }

    private static HashSet<string> PrimaryArtistTokens(CatalogSong s) => RelevanceRanker.Tokens(ArtistSplit.Split(s.Artist)[0]).ToHashSet();

    /// <summary>The upload comes from the artist themself ("Daft Punk", "Daft Punk - Topic", "DaftPunkVEVO" is not detected, that is fine).</summary>
    private static bool IsFromArtist(TrackResult r, CatalogSong s) => PrimaryArtistTokens(s).IsSubsetOf(RelevanceRanker.Tokens(r.Artist));

    private static bool AlbumByOtherArtist(CatalogSong s) => !PrimaryArtistTokens(s).IsSubsetOf(RelevanceRanker.Tokens(s.AlbumArtist));

    private static bool IsSpecialEdition(CatalogSong s) => IsSpecialEditionName(s.Album);

    /// <summary>Deluxe/anniversary/drumless... releases: valid albums, but not "the regular one".</summary>
    public static bool IsSpecialEditionName(string album) => SpecialEdition.IsMatch(album);

    internal static string CleanTitle(string title) => DashSuffix.Replace(Brackets.Replace(title, " "), string.Empty).Trim();

    internal static bool IsCompilation(CatalogSong s) =>
        NotAnAlbum.IsMatch(s.Album) || string.Equals(s.AlbumArtist, "Various Artists", StringComparison.OrdinalIgnoreCase);

    /// <summary>Albums before EPs before singles.</summary>
    internal static int ReleaseKind(CatalogSong s) =>
        s.Album.EndsWith("- Single", StringComparison.OrdinalIgnoreCase) ? 2 : s.Album.EndsWith("- EP", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static string SongKey(CatalogSong s) =>
        string.Join(' ', RelevanceRanker.Tokens(CleanTitle(s.Title))) + "|" + string.Join(' ', RelevanceRanker.Tokens(ArtistSplit.Split(s.Artist)[0]));
}
