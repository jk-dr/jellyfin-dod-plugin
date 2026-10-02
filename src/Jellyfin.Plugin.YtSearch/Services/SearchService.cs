using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Cached multi-source search plus an id registry so virtual ids can be resolved later.</summary>
public class SearchService
{
    private readonly YtDlpService _ytdlp;
    private readonly LibraryService _library;
    private readonly TrackRegistry _registry;
    private readonly ILogger<SearchService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Expires, Task<IReadOnlyList<TrackResult>> Task)> _cache = new();

    public SearchService(YtDlpService ytdlp, LibraryService library, TrackRegistry registry, ILogger<SearchService> logger)
    {
        _ytdlp = ytdlp;
        _library = library;
        _registry = registry;
        _logger = logger;
    }

    public bool TryResolve(Guid id, out TrackResult result) => _registry.TryGet(id, out result);

    /// <summary>Searches every enabled source in parallel. YouTube results come first, then SoundCloud.</summary>
    public async Task<IReadOnlyList<TrackResult>> SearchAsync(string query, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var tasks = new List<Task<IReadOnlyList<TrackResult>>>
        {
            SearchSourceAsync(Sources.YouTube, query, Math.Clamp(cfg?.MaxResults ?? 10, 0, 50), ct),
        };
        if (cfg?.EnableSoundCloud ?? true)
        {
            tasks.Add(SearchSourceAsync(Sources.SoundCloud, query, Math.Clamp(cfg?.SoundCloudMaxResults ?? 10, 0, 50), ct));
        }

        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        return all.SelectMany(r => r).ToList();
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

    /// <summary>Drops tracks that can't be downloaded (DRM, preview-only). Inconclusive checks keep the track.</summary>
    private async Task<List<TrackResult>> FilterPlayableAsync(List<TrackResult> tracks)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var gate = new SemaphoreSlim(10);
        var verdicts = await Task.WhenAll(tracks.Select(async t =>
        {
            if (_registry.GetPlayable(t.TrackId) is { } known)
            {
                return known;
            }

            await gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                var ok = await _ytdlp.CheckDownloadableAsync(t, cts.Token).ConfigureAwait(false);
                if (ok.HasValue)
                {
                    _registry.SetPlayable(t.TrackId, ok.Value);
                }

                return ok ?? true;
            }
            catch (OperationCanceledException)
            {
                return true;
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
        var raw = await _ytdlp.SearchAsync(source, query, max, cts.Token).ConfigureAwait(false);
        var results = raw.Select(_library.WithId).ToList();
        if (source == Sources.SoundCloud && (Plugin.Instance?.Configuration.HideUnplayableSoundCloud ?? true))
        {
            results = await FilterPlayableAsync(results).ConfigureAwait(false);
        }

        _registry.Add(results);

        _logger.LogInformation("{Source} search '{Query}' -> {Count} results", source, query, results.Count);
        return results;
    }
}
