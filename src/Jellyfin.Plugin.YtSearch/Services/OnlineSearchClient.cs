using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Searches YouTube and SoundCloud directly over HTTP (about 0.5 s) instead of starting yt-dlp (several seconds).
/// Both are the same public endpoints their own websites use, no API key. Returns null when the response is not in
/// the expected shape so the caller can fall back to yt-dlp.
/// </summary>
public class OnlineSearchClient
{
    private const string UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    private static readonly Regex ScriptSrc = new(@"<script[^>]+src=""(https://a-v2\.sndcdn\.com/assets/[^""]+\.js)""", RegexOptions.Compiled);
    private static readonly Regex ClientIdPattern = new(@"client_id\s*[:=]\s*""([0-9a-zA-Z]{32})""", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<OnlineSearchClient> _logger;
    private readonly SemaphoreSlim _clientIdLock = new(1, 1);
    private string? _soundCloudClientId;

    public OnlineSearchClient(ILogger<OnlineSearchClient> logger)
    {
        _logger = logger;
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<IReadOnlyList<TrackResult>?> SearchYouTubeAsync(string query, int max, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB", clientVersion = "2.20250101.00.00", hl = "en", gl = "US" } },
            query,
            @params = "EgIQAQ%3D%3D", // filter: videos only
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/search?prettyPrint=false")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "https://www.youtube.com");
        var json = await SendAsync(request, ct).ConfigureAwait(false);
        return ParseYouTube(json, max);
    }

    /// <summary>The SoundCloud profile (name and address) of an artist: the most followed account whose name is exactly the artist's.</summary>
    public async Task<(string Name, string Url)?> FindSoundCloudProfileAsync(string artist, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var clientId = await GetClientIdAsync(attempt > 0, ct).ConfigureAwait(false);
            if (clientId is null)
            {
                return null;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api-v2.soundcloud.com/search/users?q={Uri.EscapeDataString(artist)}&client_id={clientId}&limit=10");
                return ParseSoundCloudProfile(await SendAsync(request, ct).ConfigureAwait(false), artist);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden && attempt == 0)
            {
                _soundCloudClientId = null;
            }
        }

        return null;
    }

    internal static (string Name, string Url)? ParseSoundCloudProfile(string json, string artist)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("collection", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var wanted = string.Join(' ', RelevanceRanker.Tokens(artist));
        (string Name, string Url, long Followers, bool Verified)? best = null;
        foreach (var u in list.EnumerateArray())
        {
            var name = u.TryGetProperty("username", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
            var url = u.TryGetProperty("permalink_url", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (wanted.Length == 0 || string.Join(' ', RelevanceRanker.Tokens(name)) != wanted
                || InputGuard.SafePageUrl(url) is not { } safe || !Uri.TryCreate(safe, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("soundcloud.com", StringComparison.Ordinal) || uri.AbsolutePath.Trim('/').Contains('/'))
            {
                continue;
            }

            var followers = u.TryGetProperty("followers_count", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : 0;
            var verified = u.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
            if (best is null || (verified, followers).CompareTo((best.Value.Verified, best.Value.Followers)) > 0)
            {
                best = (name, safe.TrimEnd('/'), followers, verified);
            }
        }

        return best is { } b ? (InputGuard.CleanText(b.Name, 200, artist), b.Url) : null;
    }

    /// <summary>Playable SoundCloud tracks (DRM and preview-only ones are dropped when <paramref name="hideUnplayable"/>).</summary>
    public async Task<IReadOnlyList<TrackResult>?> SearchSoundCloudAsync(string query, int max, bool hideUnplayable, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var clientId = await GetClientIdAsync(attempt > 0, ct).ConfigureAwait(false);
            if (clientId is null)
            {
                return null;
            }

            // Ask for extra: hidden tracks should not leave the list short.
            var limit = Math.Min(50, hideUnplayable ? max + 15 : max);
            var url = $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={clientId}&limit={limit}&offset=0";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                return ParseSoundCloud(await SendAsync(request, ct).ConfigureAwait(false), max, hideUnplayable);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden && attempt == 0)
            {
                _logger.LogInformation("SoundCloud client id rejected, fetching a new one");
                _soundCloudClientId = null;
            }
        }

        return null;
    }

    internal static IReadOnlyList<TrackResult>? ParseYouTube(string json, int max)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGet(doc.RootElement, out var sections, "contents", "twoColumnSearchResultsRenderer", "primaryContents", "sectionListRenderer", "contents")
            || sections.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var results = new List<TrackResult>();
        foreach (var section in sections.EnumerateArray())
        {
            if (!TryGet(section, out var items, "itemSectionRenderer", "contents") || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (results.Count >= max)
                {
                    return results;
                }

                if (!item.TryGetProperty("videoRenderer", out var v))
                {
                    continue;
                }

                var id = v.TryGetProperty("videoId", out var idEl) ? idEl.GetString() : null;
                var duration = TryGet(v, out var len, "lengthText", "simpleText") ? ParseDuration(len.GetString()) : 0;
                if (!InputGuard.IsValidSourceId(Sources.YouTube, id) || duration <= 0)
                {
                    continue; // live streams and premieres have no length
                }

                var title = Runs(v, "title");
                var channel = Runs(v, "ownerText");
                var thumb = TryGet(v, out var thumbs, "thumbnail", "thumbnails") && thumbs.ValueKind == JsonValueKind.Array && thumbs.GetArrayLength() > 0
                    ? thumbs[thumbs.GetArrayLength() - 1].TryGetProperty("url", out var u) ? u.GetString() : null
                    : null;
                results.Add(new TrackResult(
                    Sources.YouTube,
                    id!,
                    InputGuard.CleanText(title, 300, id!),
                    InputGuard.CleanText(channel, 200, "YouTube"),
                    duration,
                    InputGuard.SafeThumbnailUrl(thumb) ?? $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                    $"https://www.youtube.com/watch?v={id}"));
            }
        }

        return results;
    }

    internal static IReadOnlyList<TrackResult>? ParseSoundCloud(string json, int max, bool hideUnplayable)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("collection", out var collection) || collection.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var results = new List<TrackResult>();
        foreach (var t in collection.EnumerateArray())
        {
            if (results.Count >= max)
            {
                break;
            }

            if (t.TryGetProperty("kind", out var kind) && kind.GetString() != "track")
            {
                continue;
            }

            var id = t.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : null;
            var ms = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            var page = InputGuard.SafePageUrl(t.TryGetProperty("permalink_url", out var p) ? p.GetString() : null);
            if (!InputGuard.IsValidSourceId(Sources.SoundCloud, id) || ms <= 0 || page is null)
            {
                continue;
            }

            if (hideUnplayable && !IsDownloadableSoundCloud(t))
            {
                continue;
            }

            var artist = TryGet(t, out var user, "user", "username") ? user.GetString() : null;
            var art = t.TryGetProperty("artwork_url", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()
                : TryGet(t, out var av, "user", "avatar_url") && av.ValueKind == JsonValueKind.String ? av.GetString() : null;
            art = art?.Replace("-large.", "-t500x500.", StringComparison.Ordinal);
            results.Add(new TrackResult(
                Sources.SoundCloud,
                id!,
                InputGuard.CleanText(t.TryGetProperty("title", out var title) ? title.GetString() : null, 300, id!),
                InputGuard.CleanText(artist, 200, "SoundCloud"),
                ms / 1000.0,
                InputGuard.SafeThumbnailUrl(art) ?? string.Empty,
                page));
        }

        return results;
    }

    /// <summary>
    /// Downloadable means a plain (not encrypted, not 30-second preview) AAC stream exists. DRM tracks only list
    /// "encrypted-hls" streams; preview-only tracks list streams flagged "snipped".
    /// </summary>
    internal static bool IsDownloadableSoundCloud(JsonElement track)
    {
        if (!TryGet(track, out var transcodings, "media", "transcodings") || transcodings.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var tc in transcodings.EnumerateArray())
        {
            var snipped = tc.TryGetProperty("snipped", out var s) && s.ValueKind == JsonValueKind.True;
            if (snipped || !TryGet(tc, out var format, "format"))
            {
                continue;
            }

            var protocol = format.TryGetProperty("protocol", out var pr) ? pr.GetString() : null;
            var mime = format.TryGetProperty("mime_type", out var m) ? m.GetString() : null;
            if (protocol == "hls" && mime is not null && mime.StartsWith("audio/mp4", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static double ParseDuration(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        double total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!int.TryParse(part, out var n))
            {
                return 0;
            }

            total = (total * 60) + n;
        }

        return total;
    }

    private static string? Runs(JsonElement v, string name) =>
        v.TryGetProperty(name, out var el) && el.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array
            ? string.Concat(runs.EnumerateArray().Select(r => r.TryGetProperty("text", out var t) ? t.GetString() : null))
            : null;

    private static bool TryGet(JsonElement el, out JsonElement result, params string[] path)
    {
        result = el;
        foreach (var key in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(key, out result))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new InvalidOperationException("Response too large");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxResponseBytes)
            {
                throw new InvalidOperationException("Response too large");
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>SoundCloud's website embeds a public client id in its scripts; same way yt-dlp finds it. Cached until rejected.</summary>
    private async Task<string?> GetClientIdAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _soundCloudClientId is not null)
        {
            return _soundCloudClientId;
        }

        await _clientIdLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _soundCloudClientId is not null)
            {
                return _soundCloudClientId;
            }

            using var homeRequest = new HttpRequestMessage(HttpMethod.Get, "https://soundcloud.com/");
            var html = await SendAsync(homeRequest, ct).ConfigureAwait(false);
            foreach (var script in ScriptSrc.Matches(html).Select(m => m.Groups[1].Value).Reverse())
            {
                using var scriptRequest = new HttpRequestMessage(HttpMethod.Get, script);
                var js = await SendAsync(scriptRequest, ct).ConfigureAwait(false);
                if (ClientIdPattern.Match(js) is { Success: true } match)
                {
                    _soundCloudClientId = match.Groups[1].Value;
                    return _soundCloudClientId;
                }
            }

            _logger.LogWarning("Could not find a SoundCloud client id; falling back to yt-dlp for SoundCloud search");
            return null;
        }
        finally
        {
            _clientIdLock.Release();
        }
    }
}
