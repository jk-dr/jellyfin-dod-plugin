using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Cached multi-source search, ranked by relevance and enriched with real album metadata.</summary>
public class SearchService
{
    private readonly YtDlpService _ytdlp;
    private readonly OnlineSearchClient _online;
    private readonly CatalogClient _catalog;
    private readonly LibraryService _library;
    private readonly TrackRegistry _registry;
    private readonly ILogger<SearchService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Expires, Task<IReadOnlyList<TrackResult>> Task)> _cache = new();
    private readonly object _cacheLock = new();

    // An artist page asks for more songs per source than a normal search, but skips long mixes and compilations.
    private const int ArtistPerSource = 30;
    private const double MaxSongSeconds = 15 * 60;

    public SearchService(YtDlpService ytdlp, OnlineSearchClient online, CatalogClient catalog, LibraryService library, TrackRegistry registry, ILogger<SearchService> logger)
    {
        _ytdlp = ytdlp;
        _online = online;
        _catalog = catalog;
        _library = library;
        _registry = registry;
        _logger = logger;
    }

    public bool TryResolve(Guid id, out TrackResult result) => _registry.TryGet(id, out result);

    public IReadOnlyList<TrackResult> TracksOfAlbum(Guid albumId) => _registry.TracksOfAlbum(albumId);

    /// <summary>Searches every enabled source in parallel; results come back ordered by relevance, with album metadata where known.</summary>
    public async Task<IReadOnlyList<TrackResult>> SearchAsync(string query, CancellationToken ct, int? perSource = null)
    {
        query = InputGuard.CleanQuery(query);
        if (query.Length < 3)
        {
            return Array.Empty<TrackResult>();
        }

        _catalog.Prefetch(query); // the album lookup runs while the sites are searched
        var cfg = Plugin.Instance?.Configuration;
        var tasks = new List<Task<IReadOnlyList<TrackResult>>>
        {
            SearchSourceAsync(Sources.YouTube, query, Limit(cfg?.MaxResults, perSource), ct),
        };
        if (cfg?.EnableSoundCloud ?? true)
        {
            tasks.Add(SearchSourceAsync(Sources.SoundCloud, query, Limit(cfg?.SoundCloudMaxResults, perSource), ct));
        }

        var raw = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(r => r).ToList();
        var ranked = RelevanceRanker.Rank(query, raw);
        var meta = await _catalog.GetMetaAsync(query, ranked, ct).ConfigureAwait(false);

        // A track keeps the album (and so the id) it was first given; new ones get the catalog match, or a single named after the song.
        // Songs matched to a standard release are the canonical copy, so they go first (stable: relevance order is otherwise kept).
        var canonical = new HashSet<string>();
        var finals = ranked.Select(r =>
        {
            if (_registry.TryGetBySource(r.Source, r.SourceId, out var known) && (IsCatalogMatch(known) || _library.IsPromoted(known.TrackId)))
            {
                return known;
            }

            meta.TryGetValue(r.Key, out var catalog);
            if (catalog is not null && !MetadataMatcher.IsSpecialEditionName(catalog.Album))
            {
                canonical.Add(r.Key);
            }

            return _library.WithId(r with { Meta = AlbumPolicy.For(r, catalog), ThumbnailUrl = catalog?.ArtworkUrl ?? r.ThumbnailUrl });
        }).OrderBy(r => canonical.Contains(r.Key) ? 0 : 1).ToList();
        var shown = DedupeAcrossSources(finals, r => _library.IsPromoted(r.TrackId));
        if (shown.Count < finals.Count)
        {
            _logger.LogInformation("Hid {Count} copies of songs that are on both YouTube and SoundCloud", finals.Count - shown.Count);
        }

        _registry.Add(finals);
        return shown;
    }

    /// <summary>
    /// True for a track whose album came from the catalog lookup (which can vary between searches, so it is remembered).
    /// A made-up album (named after the song) is always recomputed, so it is never stuck on an older naming.
    /// </summary>
    private static bool IsCatalogMatch(TrackResult t) => t.Meta is { } m && (m.Year is not null || m.TrackNumber is not null || m.ArtworkUrl is not null);

    private static int Limit(int? configured, int? perSource)
    {
        var max = Math.Clamp(configured ?? 10, 0, 50);
        return max > 0 && perSource is { } n ? Math.Clamp(n, 1, 50) : max;
    }

    /// <summary>Songs by an artist (for opening an artist that is not in the library yet), not already downloaded.</summary>
    public async Task<IReadOnlyList<TrackResult>> SearchArtistAsync(string artist, CancellationToken ct) =>
        FilterArtistSongs(artist, await SearchAsync(artist, ct, ArtistPerSource).ConfigureAwait(false))
            .Where(r => !_library.IsPromoted(r.TrackId))
            .ToList();

    /// <summary>Keeps results that are by the artist (channel name, credit, or "Artist - Title" title), no long mixes; YouTube first.</summary>
    internal static List<TrackResult> FilterArtistSongs(string artist, IReadOnlyList<TrackResult> results)
    {
        var wanted = RelevanceRanker.Tokens(artist).ToHashSet();
        if (wanted.Count == 0)
        {
            return new List<TrackResult>();
        }

        bool IsBy(TrackResult r) =>
            wanted.IsSubsetOf(RelevanceRanker.Tokens(r.DisplayArtist))
            || wanted.IsSubsetOf(RelevanceRanker.Tokens(r.CleanArtist))
            || wanted.IsSubsetOf(RelevanceRanker.Tokens(TitleArtistPart(r.Title)));

        return results.Where(r => IsBy(r) && r.DurationSeconds <= MaxSongSeconds).OrderBy(r => r.Source == Sources.YouTube ? 0 : 1).ToList();
    }

    /// <summary>The "Artist" of an "Artist - Title" style upload title.</summary>
    private static string TitleArtistPart(string title)
    {
        var m = Regex.Match(title ?? string.Empty, @"^(.+?)\s+[-–—]\s+.+$");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    public (TrackResult Track, string Name)? FindArtist(Guid artistId) => _registry.FindArtist(artistId);

    /// <summary>
    /// The same song on both sites: if you already have one copy, only that one is shown; otherwise YouTube wins and the
    /// SoundCloud copy is hidden. Copies on the same site are left alone.
    /// </summary>
    internal static List<TrackResult> DedupeAcrossSources(IReadOnlyList<TrackResult> results, Func<TrackResult, bool> isDownloaded)
    {
        var hide = new HashSet<string>();
        foreach (var sc in results.Where(r => r.Source == Sources.SoundCloud))
        {
            var copies = results.Where(y => y.Source == Sources.YouTube && AreSameSong(y, sc)).ToList();
            if (copies.Count == 0)
            {
                continue;
            }

            if (isDownloaded(sc))
            {
                // Already have it from SoundCloud: don't offer a second download from YouTube.
                foreach (var y in copies.Where(y => !isDownloaded(y)))
                {
                    hide.Add(y.Key);
                }
            }
            else
            {
                hide.Add(sc.Key); // a YouTube copy exists (downloaded or not): YouTube is preferred
            }
        }

        return results.Where(r => !hide.Contains(r.Key)).ToList();
    }

    /// <summary>
    /// Same title once brackets, "feat." parts and filler like "official audio" are ignored (either may carry the artist's
    /// name too: "Artist - Title" vs "Title"), similar length, and neither is a remix/cover/live version the other is not.
    /// </summary>
    internal static bool AreSameSong(TrackResult a, TrackResult b)
    {
        // Different lengths are different recordings (radio edit, extended mix, a live take): both stay.
        if (a.DurationSeconds > 0 && b.DurationSeconds > 0
            && Math.Abs(a.DurationSeconds - b.DurationSeconds) > Math.Max(5, 0.03 * Math.Max(a.DurationSeconds, b.DurationSeconds)))
        {
            return false;
        }

        if (!NoiseWords(a).SetEquals(NoiseWords(b)))
        {
            return false;
        }

        // "Artist - Song" and "Song - Artist" titles: the artist's own words are not part of the song's name.
        var artistsA = ArtistWords(a);
        var artistsB = ArtistWords(b);
        var artists = artistsA.Union(artistsB).ToHashSet();
        var ta = SongWords(a, artists);
        var tb = SongWords(b, artists);
        var (small, large) = ta.Count <= tb.Count ? (ta, tb) : (tb, ta);
        if (small.Count >= 2)
        {
            return small.IsSubsetOf(large);
        }

        // A one-word song name ("Believe") only matches the same word by the same artist: the credits overlap, or one
        // title names the other upload's artist ("Cher - Believe" next to "Believe" by Cher).
        var empty = new HashSet<string>();
        var namesOtherArtist = SongWords(a, empty).Overlaps(artistsB) || SongWords(b, empty).Overlaps(artistsA);
        return small.Count == 1 && small.SetEquals(large) && (artistsA.Overlaps(artistsB) || namesOtherArtist);
    }

    private static readonly Regex FeaturingTail = new(@"\s(?:feat\.?|ft\.?|featuring|with)\s.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracketGroups = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);

    private static HashSet<string> SongWords(TrackResult r, ISet<string> artistWords)
    {
        var title = BracketGroups.Replace(FeaturingTail.Replace(r.DisplayTitle, string.Empty), " ");
        var words = RelevanceRanker.Tokens(title).Where(w => !MetadataMatcher.IsFillerWord(w)).ToHashSet();
        var withoutArtist = words.Where(w => !artistWords.Contains(w)).ToHashSet();
        return withoutArtist.Count > 0 ? withoutArtist : words; // a song named like its artist keeps its words
    }

    private static HashSet<string> ArtistWords(TrackResult r) =>
        RelevanceRanker.Tokens(string.Join(" ", r.ArtistNames)).Where(w => !MetadataMatcher.IsFillerWord(w)).ToHashSet();

    private static HashSet<string> NoiseWords(TrackResult r) =>
        RelevanceRanker.Tokens(r.DisplayTitle).Where(RelevanceRanker.IsNoiseWord).ToHashSet();

    /// <summary>One source; a failure is logged and yields no results so the other sources still show.</summary>
    private async Task<IReadOnlyList<TrackResult>> SearchSourceAsync(string source, string query, int max, CancellationToken ct)
    {
        if (max == 0)
        {
            return Array.Empty<TrackResult>();
        }

        try
        {
            var ttl = TimeSpan.FromMinutes(Math.Max(Plugin.Instance?.Configuration.SearchCacheMinutes ?? 5, 0));
            var key = $"{source}|{query.Trim().ToLowerInvariant()}|{max}";
            Task<IReadOnlyList<TrackResult>> task;
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(key, out var entry) || entry.Expires < DateTime.UtcNow || entry.Task.IsFaulted)
                {
                    PruneCache();

                    // Not tied to the request token: a cancelled request shouldn't poison the shared task.
                    entry = (DateTime.UtcNow + ttl, RunAsync(source, query, max));
                    _cache[key] = entry;
                }

                task = entry.Task;
            }

            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Source} search for '{Query}' failed", source, query);
            return Array.Empty<TrackResult>();
        }
    }

    /// <summary>Keeps the per-query cache from growing without bound.</summary>
    private void PruneCache()
    {
        if (_cache.Count < 500)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var kv in _cache.Where(kv => kv.Value.Expires < now).ToList())
        {
            _cache.TryRemove(kv.Key, out _);
        }

        if (_cache.Count >= 500)
        {
            _cache.Clear();
        }
    }

    /// <summary>Drops tracks that can't be downloaded (DRM, preview-only). Inconclusive checks keep the track.</summary>
    private async Task<List<TrackResult>> FilterPlayableAsync(List<TrackResult> tracks)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var gate = new SemaphoreSlim(10);
        var verdicts = await Task.WhenAll(tracks.Select(async t =>
        {
            if (_registry.GetPlayable(t.Source, t.SourceId) is { } known)
            {
                return known;
            }

            try
            {
                await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return true;
            }

            try
            {
                var ok = await _ytdlp.CheckDownloadableAsync(t, cts.Token).ConfigureAwait(false);
                if (ok.HasValue)
                {
                    _registry.SetPlayable(t.Source, t.SourceId, ok.Value);
                }

                return ok ?? true;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        var kept = tracks.Where((_, i) => verdicts[i]).ToList();
        if (kept.Count < tracks.Count)
        {
            _logger.LogInformation("Hid {Count} undownloadable SoundCloud results (DRM or preview-only)", tracks.Count - kept.Count);
        }

        return kept;
    }

    private async Task<IReadOnlyList<TrackResult>> RunAsync(string source, string query, int max)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(Plugin.Instance?.Configuration.SearchTimeoutSeconds ?? 8, 1));
        using var cts = new CancellationTokenSource(timeout);
        var hide = source == Sources.SoundCloud && (Plugin.Instance?.Configuration.HideUnplayableSoundCloud ?? true);

        // Fast path: ask the site's search endpoint directly. Anything unexpected falls back to yt-dlp.
        IReadOnlyList<TrackResult>? raw = null;
        var viaApi = false;
        try
        {
            raw = source == Sources.SoundCloud
                ? await _online.SearchSoundCloudAsync(query, max, hide, cts.Token).ConfigureAwait(false)
                : await _online.SearchYouTubeAsync(query, max, cts.Token).ConfigureAwait(false);
            viaApi = raw is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
        {
            _logger.LogInformation("{Source} direct search failed ({Message}); using yt-dlp", source, ex.Message);
        }

        if (raw is null)
        {
            using var fallback = new CancellationTokenSource(timeout);
            raw = await _ytdlp.SearchAsync(source, query, max, fallback.Token).ConfigureAwait(false);
        }

        var results = raw.ToList();
        if (hide && !viaApi)
        {
            // yt-dlp's flat search does not say which tracks are DRM/preview-only, so check them one by one.
            results = await FilterPlayableAsync(results).ConfigureAwait(false);
        }

        _logger.LogInformation("{Source} search '{Query}' -> {Count} results ({Via})", source, query, results.Count, viaApi ? "direct" : "yt-dlp");
        return results;
    }
}
