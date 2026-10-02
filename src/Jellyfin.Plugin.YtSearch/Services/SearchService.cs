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
    private const int MaxRegistry = 5000;

    private readonly YtDlpService _ytdlp;
    private readonly ILogger<SearchService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Expires, Task<IReadOnlyList<TrackResult>> Task)> _cache = new();
    private readonly ConcurrentDictionary<Guid, TrackResult> _registry = new();

    public SearchService(YtDlpService ytdlp, ILogger<SearchService> logger)
    {
        _ytdlp = ytdlp;
        _logger = logger;
    }

    public bool TryResolve(Guid id, out TrackResult result) => _registry.TryGetValue(id, out result!);

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

    private async Task<IReadOnlyList<TrackResult>> RunAsync(string source, string query, int max)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(Plugin.Instance?.Configuration.SearchTimeoutSeconds ?? 8, 1));
        using var cts = new CancellationTokenSource(timeout);
        var results = await _ytdlp.SearchAsync(source, query, max, cts.Token).ConfigureAwait(false);
        if (_registry.Count > MaxRegistry)
        {
            _registry.Clear();
        }

        foreach (var r in results)
        {
            _registry[r.TrackId] = r;
            _registry[r.AlbumId] = r;
        }

        _logger.LogInformation("{Source} search '{Query}' -> {Count} results", source, query, results.Count);
        return results;
    }
}
