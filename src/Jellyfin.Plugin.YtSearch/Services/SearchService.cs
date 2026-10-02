using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Searches every enabled source in parallel; results come back ordered by relevance, with album metadata where known.</summary>
    public async Task<IReadOnlyList<TrackResult>> SearchAsync(string query, CancellationToken ct)
    {
        query = InputGuard.CleanQuery(query);
        if (query.Length < 3)
        {
            return Array.Empty<TrackResult>();
        }

        var cfg = Plugin.Instance?.Configuration;
        var tasks = new List<Task<IReadOnlyList<TrackResult>>>
        {
            SearchSourceAsync(Sources.YouTube, query, Math.Clamp(cfg?.MaxResults ?? 10, 0, 50), ct),
        };
        if (cfg?.EnableSoundCloud ?? true)
        {
            tasks.Add(SearchSourceAsync(Sources.SoundCloud, query, Math.Clamp(cfg?.SoundCloudMaxResults ?? 10, 0, 50), ct));
        }

        var raw = (await Task.WhenAll(tasks).ConfigureAwait(false)).SelectMany(r => r).ToList();
        var ranked = RelevanceRanker.Rank(query, raw);
        var meta = await _catalog.GetMetaAsync(query, ranked, ct).ConfigureAwait(false);

        // A track keeps the album (and so the id) it was first given; new ones get the catalog match if there is one.
        // Songs with a real album are the canonical copy, so they go first (stable: relevance order is otherwise kept).
        var finals = ranked.Select(r =>
        {
            if (_registry.TryGetBySource(r.Source, r.SourceId, out var known))
            {
                return known;
            }

            return _library.WithId(meta.TryGetValue(r.Key, out var m)
                ? r with { Meta = m, ThumbnailUrl = m.ArtworkUrl ?? r.ThumbnailUrl }
                : r);
        }).OrderBy(r => r.Meta is { } m && !MetadataMatcher.IsSpecialEditionName(m.Album) ? 0 : 1).ToList();
        _registry.Add(finals);
        return finals;
    }

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
            if (!_cache.TryGetValue(key, out var entry) || entry.Expires < DateTime.UtcNow || entry.Task.IsFaulted)
            {
                PruneCache();

                // Not tied to the request token: a cancelled request shouldn't poison the shared task.
                entry = (DateTime.UtcNow + ttl, RunAsync(source, query, max));
                _cache[key] = entry;
            }

            return await entry.Task.WaitAsync(ct).ConfigureAwait(false);
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
