using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Looks up real song metadata (album, track number, year, cover art) in Apple's public iTunes Search API, which needs
/// no key. One request per search, cached. Any failure just means results stay loose tracks without an album.
/// </summary>
public class CatalogClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ILogger<CatalogClient> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Expires, Task<IReadOnlyList<CatalogSong>> Task)> _cache = new();
    private readonly object _cacheLock = new();
    private DateTime _blockedUntil = DateTime.MinValue;

    public CatalogClient(ILogger<CatalogClient> logger)
    {
        _logger = logger;
    }

    /// <summary>Starts the lookup now (shares the cache), so it runs while the site searches do.</summary>
    public void Prefetch(string query)
    {
        if ((Plugin.Instance?.Configuration.LookUpAlbums ?? true) && DateTime.UtcNow >= _blockedUntil)
        {
            _ = SongsFor(query);
        }
    }

    private Task<IReadOnlyList<CatalogSong>> SongsFor(string query)
    {
        var key = query.Trim().ToLowerInvariant();
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out var entry) || entry.Expires < DateTime.UtcNow || entry.Task.IsFaulted)
            {
                if (_cache.Count > 300)
                {
                    _cache.Clear();
                }

                entry = (DateTime.UtcNow + CacheTtl, FetchAsync(query));
                _cache[key] = entry;
            }

            return entry.Task;
        }
    }

    /// <summary>Metadata keyed by <see cref="TrackResult.Key"/>. Waits at most a few seconds; never throws.</summary>
    public async Task<Dictionary<string, TrackMeta>> GetMetaAsync(string query, IReadOnlyList<TrackResult> ranked, CancellationToken ct)
    {
        if (!(Plugin.Instance?.Configuration.LookUpAlbums ?? true) || ranked.Count == 0 || DateTime.UtcNow < _blockedUntil)
        {
            return new Dictionary<string, TrackMeta>();
        }

        try
        {
            var songs = await SongsFor(query).WaitAsync(MaxWait, ct).ConfigureAwait(false);
            return MetadataMatcher.Match(ranked, songs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            _logger.LogInformation("Album lookup took over {Seconds}s; results stay without albums this time", MaxWait.TotalSeconds);
            return new Dictionary<string, TrackMeta>();
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Album lookup failed ({Message}); results stay without albums", ex.Message);
            return new Dictionary<string, TrackMeta>();
        }
    }

    private async Task<IReadOnlyList<CatalogSong>> FetchAsync(string query)
    {
        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=song&limit=50";
        using var response = await _http.GetAsync(url).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            _blockedUntil = DateTime.UtcNow.AddMinutes(2); // rate limited: stop asking for a bit
        }

        response.EnsureSuccessStatusCode();
        return ParseSongs(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    internal static IReadOnlyList<CatalogSong> ParseSongs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var songs = new List<CatalogSong>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return songs;
        }

        foreach (var r in results.EnumerateArray())
        {
            if (Str(r, "kind") != "song")
            {
                continue;
            }

            var title = Str(r, "trackName");
            var artist = Str(r, "artistName");
            var album = Str(r, "collectionName");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            {
                continue;
            }

            var art = Str(r, "artworkUrl100")?.Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
            songs.Add(new CatalogSong(
                InputGuard.CleanText(title, 300, "Unknown"),
                InputGuard.CleanText(artist, 200, "Unknown"),
                InputGuard.CleanText(album, 200, "Unknown"),
                InputGuard.CleanText(Str(r, "collectionArtistName") ?? artist, 200, "Unknown"),
                Year(Str(r, "releaseDate")),
                Int(r, "trackNumber"),
                Int(r, "discNumber"),
                Str(r, "primaryGenreName"),
                (Int(r, "trackTimeMillis") ?? 0) / 1000.0,
                InputGuard.SafeThumbnailUrl(art)));
        }

        return songs;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n is >= 0 and <= int.MaxValue ? (int)n : null;

    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) && y is > 1000 and < 3000 ? y : null;
}
