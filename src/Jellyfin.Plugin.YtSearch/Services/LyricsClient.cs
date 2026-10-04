using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Lyrics of a song: <see cref="Synced"/> is LRC text with timestamps, <see cref="Plain"/> has none.</summary>
public record Lyrics(string? Synced, string? Plain);

/// <summary>
/// Looks a song up on lrclib.net (free, no key) for timed lyrics. Best effort: a short timeout and any failure just means
/// no lyrics. Only artist, title, album and length are sent.
/// </summary>
public class LyricsClient
{
    private const string Base = "https://lrclib.net/api/";
    private const int MaxLyricsChars = 200_000;

    private readonly HttpClient _http;
    private readonly ILogger<LyricsClient> _logger;

    public LyricsClient(ILogger<LyricsClient> logger)
    {
        _logger = logger;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.YtSearch (personal use)");
    }

    public async Task<Lyrics?> FetchAsync(TrackResult track, CancellationToken ct)
    {
        if (!(Plugin.Instance?.Configuration.FetchLyrics ?? true))
        {
            return null;
        }

        var title = track.Meta?.Title ?? track.DisplayTitle;
        var artist = track.ArtistNames.FirstOrDefault() ?? track.DisplayArtist;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var exact = $"get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}"
                + (track.Meta?.Album is { Length: > 0 } album ? $"&album_name={Uri.EscapeDataString(album)}" : string.Empty)
                + (track.DurationSeconds > 0 ? $"&duration={(int)Math.Round((double)track.DurationSeconds)}" : string.Empty);
            var found = ParseOne(await GetJsonAsync(exact, cts.Token).ConfigureAwait(false));
            if (found is null)
            {
                var search = $"search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
                found = PickBest(await GetJsonAsync(search, cts.Token).ConfigureAwait(false), (int)track.DurationSeconds);
            }

            return found;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
        {
            _logger.LogInformation("No lyrics for {Id}: {Message}", track.SourceId, ex.Message);
            return null;
        }
    }

    private async Task<string?> GetJsonAsync(string relative, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Base + relative, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
    }

    internal static Lyrics? ParseOne(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    /// <summary>The search result whose length is closest to the song's (within 5 s), preferring timed lyrics.</summary>
    internal static Lyrics? PickBest(string? json, int durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = new List<(Lyrics L, double Diff)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (FromElement(e) is not { } l)
            {
                continue;
            }

            var diff = durationSeconds > 0 && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? Math.Abs(d.GetDouble() - durationSeconds)
                : (durationSeconds > 0 ? double.MaxValue : 0);
            if (diff <= 5)
            {
                candidates.Add((l, diff));
            }
        }

        return candidates.OrderBy(c => c.L.Synced is null).ThenBy(c => c.Diff).Select(c => c.L).FirstOrDefault();
    }

    private static Lyrics? FromElement(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || (e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True))
        {
            return null;
        }

        var synced = Text(e, "syncedLyrics");
        var plain = Text(e, "plainLyrics");
        return synced is null && plain is null ? null : new Lyrics(synced, plain);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= MaxLyricsChars } s && !string.IsNullOrWhiteSpace(s) ? s : null;

    /// <summary>The sidecar Jellyfin reads next to the song: .lrc when timed, .txt otherwise.</summary>
    internal static (string Extension, string Content)? ForSidecar(Lyrics? l) =>
        l?.Synced is { } s ? (".lrc", s) : l?.Plain is { } p ? (".txt", p) : null;
}
