using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Cached YouTube search plus an id registry so virtual ids can be resolved later.</summary>
public class SearchService
{
    private const int MaxRegistry = 5000;

    private readonly YtDlpService _ytdlp;
    private readonly ILogger<SearchService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Expires, Task<IReadOnlyList<YtResult>> Task)> _cache = new();
    private readonly ConcurrentDictionary<Guid, YtResult> _registry = new();

    public SearchService(YtDlpService ytdlp, ILogger<SearchService> logger)
    {
        _ytdlp = ytdlp;
        _logger = logger;
    }

    public bool TryResolve(Guid id, out YtResult result) => _registry.TryGetValue(id, out result!);

    public async Task<IReadOnlyList<YtResult>> SearchAsync(string query, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var max = Math.Clamp(cfg?.MaxResults ?? 10, 1, 50);
        var ttl = TimeSpan.FromMinutes(Math.Max(cfg?.SearchCacheMinutes ?? 5, 0));
        var key = query.Trim().ToLowerInvariant() + "|" + max;

        if (!_cache.TryGetValue(key, out var entry) || entry.Expires < DateTime.UtcNow || entry.Task.IsFaulted)
        {
            // Not tied to the request token: a cancelled request shouldn't poison the shared task.
            var task = RunAsync(query, max);
            entry = (DateTime.UtcNow + ttl, task);
            _cache[key] = entry;
        }

        return await entry.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<YtResult>> RunAsync(string query, int max)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(Plugin.Instance?.Configuration.SearchTimeoutSeconds ?? 8, 1));
        using var cts = new CancellationTokenSource(timeout);
        var results = await _ytdlp.SearchAsync(query, max, cts.Token).ConfigureAwait(false);
        if (_registry.Count > MaxRegistry)
        {
            _registry.Clear();
        }

        foreach (var r in results)
        {
            _registry[r.TrackId] = r;
            _registry[r.AlbumId] = r;
        }

        _logger.LogInformation("YouTube search '{Query}' -> {Count} results", query, results.Count);
        return results;
    }
}
