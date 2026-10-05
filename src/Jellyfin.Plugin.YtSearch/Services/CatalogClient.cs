using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Looks up real song metadata (album, track number, year, cover art) in Apple's public iTunes Search API, which needs
/// no key. One request per search, cached. Any failure just means results stay loose tracks without an album.
/// </summary>
/// <summary>An album of an artist in the music catalog.</summary>
public sealed record CatalogAlbum(long CollectionId, string Name, int? Year, string? ArtworkUrl, string? Genre);

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
                InputGuard.SafeThumbnailUrl(art),
                Long(r, "trackId"),
                Long(r, "collectionId")));
        }

        return songs;
    }

    // ---- Discographies: an artist's albums and the songs of an album (for artist and album pages) ----

    private static readonly TimeSpan DiscographyTtl = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<string, (DateTime Expires, Task<object> Task)> _discography = new();

    /// <summary>The albums the catalog has for an artist (exact name), newest first. Empty on any failure.</summary>
    public async Task<IReadOnlyList<CatalogAlbum>> AlbumsOfArtistAsync(string artist, CancellationToken ct)
    {
        var task = Cached("albums:" + artist.Trim().ToLowerInvariant(), async () =>
        {
            var found = ParseArtistId(await GetAsync($"https://itunes.apple.com/search?term={Uri.EscapeDataString(artist)}&media=music&entity=musicArtist&limit=10").ConfigureAwait(false), artist);
            if (found is null)
            {
                return (IReadOnlyList<CatalogAlbum>)Array.Empty<CatalogAlbum>();
            }

            return ParseAlbums(await GetAsync($"https://itunes.apple.com/lookup?id={found}&entity=album&limit=200").ConfigureAwait(false), artist);
        });
        return await AwaitOrEmpty<CatalogAlbum>(task, ct).ConfigureAwait(false);
    }

    /// <summary>The catalog's exact spelling of an artist's name when the term is exactly an artist's name, else null.</summary>
    public async Task<string?> FindArtistNameAsync(string term, CancellationToken ct)
    {
        var task = Cached("artist:" + term.Trim().ToLowerInvariant(), async () =>
        {
            var json = await GetAsync($"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&media=music&entity=musicArtist&limit=10").ConfigureAwait(false);
            return (IReadOnlyList<string>)(ParseArtistName(json, term) is { } name ? new[] { name } : Array.Empty<string>());
        });
        return (await AwaitOrEmpty<string>(task, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    internal static string? ParseArtistName(string json, string artist)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var wanted = ArtistProfileService.TitleKey(artist);
        foreach (var r in results.EnumerateArray())
        {
            if (Str(r, "wrapperType") == "artist" && Str(r, "artistName") is { } name && ArtistProfileService.TitleKey(name).SetEquals(wanted))
            {
                return InputGuard.CleanText(name, 200, artist);
            }
        }

        return null;
    }

    /// <summary>The songs of a catalog album in album order. Empty on any failure.</summary>
    public async Task<IReadOnlyList<CatalogSong>> SongsOfAlbumAsync(long collectionId, CancellationToken ct)
    {
        var task = Cached("songs:" + collectionId, async () =>
            ParseAlbumSongs(await GetAsync($"https://itunes.apple.com/lookup?id={collectionId}&entity=song&limit=200").ConfigureAwait(false)));
        return await AwaitOrEmpty<CatalogSong>(task, ct).ConfigureAwait(false);
    }

    private Task<object> CachedObject(string key, Func<Task<object>> factory)
    {
        lock (_cacheLock)
        {
            if (!_discography.TryGetValue(key, out var entry) || entry.Expires < DateTime.UtcNow || entry.Task.IsFaulted)
            {
                if (_discography.Count > 300)
                {
                    _discography.Clear();
                }

                entry = (DateTime.UtcNow + DiscographyTtl, factory());
                _discography[key] = entry;
            }

            return entry.Task;
        }
    }

    private Task<object> Cached<T>(string key, Func<Task<IReadOnlyList<T>>> factory) =>
        CachedObject(key, async () => (object)await factory().ConfigureAwait(false));

    private async Task<IReadOnlyList<T>> AwaitOrEmpty<T>(Task<object> task, CancellationToken ct)
    {
        try
        {
            return (IReadOnlyList<T>)await task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Catalog lookup failed ({Message})", ex.Message);
            return Array.Empty<T>();
        }
    }

    private async Task<string> GetAsync(string url)
    {
        if (DateTime.UtcNow < _blockedUntil)
        {
            throw new HttpRequestException("The catalog is rate limiting; waiting a bit");
        }

        using var response = await _http.GetAsync(url).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            _blockedUntil = DateTime.UtcNow.AddMinutes(2);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>The id of the artist whose name is exactly the wanted one (first result wins).</summary>
    internal static long? ParseArtistId(string json, string artist)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var wanted = ArtistProfileService.TitleKey(artist);
        foreach (var r in results.EnumerateArray())
        {
            if (Str(r, "wrapperType") == "artist" && Str(r, "artistName") is { } name && ArtistProfileService.TitleKey(name).SetEquals(wanted)
                && r.TryGetProperty("artistId", out var id) && id.TryGetInt64(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static readonly Regex EditionBracket = new(@"\s*[\(\[][^)\]]*(deluxe|edition|version|expanded|remaster|bonus)[^)\]]*[\)\]]\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The artist's albums and EPs (no singles), newest first, each name once. A standard edition is preferred; when the
    /// catalog only has a special edition ("Take Care (Deluxe)"), that one is used under the plain name. Collaboration
    /// albums ("Drake &amp; 21 Savage") count.
    /// </summary>
    internal static IReadOnlyList<CatalogAlbum> ParseAlbums(string json, string artist)
    {
        using var doc = JsonDocument.Parse(json);
        var albums = new List<(CatalogAlbum Album, bool Special)>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CatalogAlbum>();
        }

        var wanted = ArtistProfileService.TitleKey(artist);
        foreach (var r in results.EnumerateArray())
        {
            var name = Str(r, "collectionName");
            if (Str(r, "wrapperType") != "collection" || Str(r, "collectionType") != "Album" || string.IsNullOrWhiteSpace(name)
                || !r.TryGetProperty("collectionId", out var idElement) || !idElement.TryGetInt64(out var id)
                || name.EndsWith("- Single", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var by = ArtistProfileService.TitleKey(Str(r, "artistName"));
            if (!wanted.IsSubsetOf(by) || by.Count > wanted.Count + 3)
            {
                continue;
            }

            var special = MetadataMatcher.IsSpecialEditionName(name);
            var plain = special ? EditionBracket.Replace(name, string.Empty).Trim() : name;
            var clean = InputGuard.CleanText(plain.Length > 0 ? plain : name, 200, "Unknown");
            var art = Str(r, "artworkUrl100")?.Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
            albums.Add((new CatalogAlbum(id, clean, Year(Str(r, "releaseDate")), InputGuard.SafeThumbnailUrl(art), Str(r, "primaryGenreName")), special));
        }

        var chosen = new List<(CatalogAlbum Album, bool Special)>();
        foreach (var candidate in albums.OrderBy(a => a.Special).ThenBy(a => a.Album.Year ?? 9999))
        {
            if (!chosen.Any(c => ArtistProfileService.NamesMatch(c.Album.Name, candidate.Album.Name)))
            {
                chosen.Add(candidate);
            }
        }

        return chosen.Select(c => c.Album).OrderByDescending(a => a.Year ?? 0).ThenBy(a => a.Name).Take(40).ToList();
    }

    /// <summary>The songs of an album lookup, in disc and track order.</summary>
    internal static IReadOnlyList<CatalogSong> ParseAlbumSongs(string json) =>
        ParseSongs(json).OrderBy(s => s.DiscNumber ?? 1).ThenBy(s => s.TrackNumber ?? 999).ToList();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static string? StrOf(JsonElement e, string name) => Str(e, name);

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n is > 0 and < 1_000_000_000_000_000 ? n : 0;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n is >= 0 and <= int.MaxValue ? (int)n : null;

    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) && y is > 1000 and < 3000 ? y : null;
}
