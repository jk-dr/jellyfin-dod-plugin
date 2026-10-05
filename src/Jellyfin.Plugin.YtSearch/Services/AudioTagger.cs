using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Writes title/artist/album/cover tags into the downloaded file with ffmpeg stream copy (no re-encoding, no quality loss),
/// so Jellyfin's normal library scan builds proper albums, artists and artwork from it.
/// </summary>
public class AudioTagger
{
    private const int MaxImageBytes = 5 * 1024 * 1024;

    private readonly IMediaEncoder _encoder;
    private readonly ILogger<AudioTagger> _logger;

    public AudioTagger(IMediaEncoder encoder, ILogger<AudioTagger> logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    /// <summary>Returns the tagged file's path (inside <paramref name="workDir"/>), or the input if tagging failed.</summary>
    public async Task<string> TagAsync(string input, TrackResult track, string workDir, CancellationToken ct)
    {
        var output = Path.Combine(workDir, "tagged.m4a");
        try
        {
            var coverTask = PrepareCoverAsync(track, workDir, ct);
            var loudnessTask = Plugin.Instance?.Configuration.NormalizeAudio ?? true ? MeasureLoudnessAsync(input, ct) : Task.FromResult<Loudness?>(null);
            var cover = await coverTask.ConfigureAwait(false);
            var loudness = await loudnessTask.ConfigureAwait(false);
            var args = new List<string> { "-y", "-loglevel", "error", "-i", input };
            if (cover is not null)
            {
                args.AddRange(new[] { "-i", cover });
            }

            args.AddRange(new[] { "-map", "0:a:0" });
            if (cover is not null)
            {
                args.AddRange(new[] { "-map", "1:v:0", "-disposition:v:0", "attached_pic" });
            }

            args.AddRange(new[] { "-c", "copy", "-map_metadata", "-1", "-movflags", "+faststart+use_metadata_tags" });
            foreach (var (key, value) in Tags(track).Concat(SourceTags(track, Path.Combine(workDir, "audio.info.json"))).Concat(ReplayGainTags(loudness)))
            {
                args.Add("-metadata");
                args.Add($"{key}={value}");
            }

            args.Add(output);
            await RunFfmpegAsync(args, ct).ConfigureAwait(false);
            return File.Exists(output) && new FileInfo(output).Length > 0 ? output : input;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Tagging {Id} failed ({Message}); using the untagged file", track.SourceId, ex.Message);
            return input;
        }
    }

    /// <summary>
    /// What the site itself knew about the song, kept in the file when the catalog did not say: release year and genre, plus
    /// the address it came from. Never overrides a tag we already have.
    /// </summary>
    internal static IEnumerable<(string Key, string Value)> SourceTags(TrackResult track, string infoJsonPath)
    {
        var have = Tags(track).Select(t => t.Key).ToHashSet();
        var info = ReadSourceInfo(infoJsonPath);
        if (!have.Contains("date") && info.Year is { } y)
        {
            yield return ("date", y.ToString(CultureInfo.InvariantCulture));
        }

        if (!have.Contains("genre") && info.Genre is { } g)
        {
            yield return ("genre", g);
        }

        if (InputGuard.SafePageUrl(track.PageUrl) is { } url)
        {
            yield return ("comment", url);
        }
    }

    internal record SourceInfo(int? Year, string? Genre);

    internal static SourceInfo ReadSourceInfo(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 20 * 1024 * 1024)
            {
                return new SourceInfo(null, null);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ParseSourceInfo(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new SourceInfo(null, null);
        }
    }

    internal static SourceInfo ParseSourceInfo(JsonElement root)
    {
        int? year = null;
        if (root.TryGetProperty("release_year", out var ry) && ry.ValueKind == JsonValueKind.Number && ry.TryGetInt32(out var yi))
        {
            year = yi;
        }
        else if (root.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String
            && rd.GetString() is { Length: >= 4 } date && int.TryParse(date.AsSpan(0, 4), out var dy))
        {
            year = dy;
        }

        if (year is < 1900 or > 2100)
        {
            year = null;
        }

        string? genre = null;
        if (root.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array && gs.GetArrayLength() > 0 && gs[0].ValueKind == JsonValueKind.String)
        {
            genre = gs[0].GetString();
        }
        else if (root.TryGetProperty("genre", out var g) && g.ValueKind == JsonValueKind.String)
        {
            genre = g.GetString();
        }

        genre = string.IsNullOrWhiteSpace(genre) ? null : InputGuard.CleanText(genre, 60, "");
        return new SourceInfo(year, string.IsNullOrEmpty(genre) ? null : genre);
    }

    /// <summary>Measured loudness: integrated LUFS and true peak in dBTP.</summary>
    internal record Loudness(double Lufs, double TruePeakDb);

    /// <summary>ReplayGain 2.0 reference level.</summary>
    private const double ReferenceLufs = -18.0;

    /// <summary>One fast analysis pass (no output file); null when it fails, so the song is simply left alone.</summary>
    private async Task<Loudness?> MeasureLoudnessAsync(string input, CancellationToken ct)
    {
        try
        {
            var log = await RunFfmpegAsync(
                new[] { "-hide_banner", "-nostats", "-loglevel", "info", "-i", input, "-map", "0:a:0", "-af", "ebur128=peak=true", "-f", "null", "-" },
                ct, TimeSpan.FromSeconds(40)).ConfigureAwait(false);
            return ParseLoudness(log);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Loudness measurement failed: {Message}", ex.Message);
            return null;
        }
    }

    private static readonly Regex SummaryLufs = new(@"Integrated loudness:\s+I:\s+(-?\d+(?:\.\d+)?) LUFS", RegexOptions.Compiled);
    private static readonly Regex SummaryPeak = new(@"True peak:\s+Peak:\s+(-?\d+(?:\.\d+)?) dBFS", RegexOptions.Compiled);

    /// <summary>Reads the summary the ebur128 filter prints at the end of ffmpeg's log (silence prints "-inf" and gives null).</summary>
    internal static Loudness? ParseLoudness(string log)
    {
        var summary = log.LastIndexOf("Summary:", StringComparison.Ordinal);
        if (summary < 0)
        {
            return null;
        }

        var text = log[summary..];
        if (SummaryLufs.Match(text) is not { Success: true } i || SummaryPeak.Match(text) is not { Success: true } p
            || !double.TryParse(i.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs)
            || !double.TryParse(p.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var peak)
            || lufs < -70 || lufs > 0)
        {
            return null;
        }

        return new Loudness(lufs, peak);
    }

    internal static IEnumerable<(string Key, string Value)> ReplayGainTags(Loudness? l)
    {
        if (l is null)
        {
            yield break;
        }

        var gain = Math.Clamp(ReferenceLufs - l.Lufs, -30, 30);
        yield return ("REPLAYGAIN_TRACK_GAIN", gain.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture) + " dB");
        yield return ("REPLAYGAIN_TRACK_PEAK", Math.Pow(10, l.TruePeakDb / 20).ToString("0.000000", CultureInfo.InvariantCulture));
        yield return ("REPLAYGAIN_REFERENCE_LOUDNESS", "-18.00 LUFS");
    }

    internal static IEnumerable<(string Key, string Value)> Tags(TrackResult track)
    {
        var m = track.Meta;
        yield return ("title", m?.Title ?? track.Title);
        yield return ("artist", string.Join("; ", track.ArtistNames));
        yield return ("album_artist", track.PrimaryAlbumArtist);
        if (m is null)
        {
            yield break;
        }

        yield return ("album", m.Album);
        if (m.TrackNumber is { } t)
        {
            yield return ("track", t.ToString());
        }

        if (m.DiscNumber is { } d)
        {
            yield return ("disc", d.ToString());
        }

        if (m.Year is { } y)
        {
            yield return ("date", y.ToString());
        }

        if (!string.IsNullOrWhiteSpace(m.Genre))
        {
            yield return ("genre", m.Genre!);
        }
    }

    /// <summary>Downloads the artwork (allowlisted hosts only) and crops it to a 600x600 square, like album art.</summary>
    private async Task<string?> PrepareCoverAsync(TrackResult track, string workDir, CancellationToken ct)
    {
        if (InputGuard.SafeThumbnailUrl(track.ThumbnailUrl) is not { } url)
        {
            return null;
        }

        try
        {
            var raw = Path.Combine(workDir, "cover-raw");
            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) })
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
                {
                    return null;
                }

                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(raw);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxImageBytes)
                    {
                        return null;
                    }

                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            var cover = Path.Combine(workDir, "cover.jpg");
            await RunFfmpegAsync(
                new[] { "-y", "-loglevel", "error", "-i", raw, "-vf", "crop='min(iw,ih)':'min(iw,ih)',scale=600:600:flags=lanczos", "-frames:v", "1", "-q:v", "3", cover },
                ct).ConfigureAwait(false);
            return File.Exists(cover) ? cover : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            _logger.LogInformation("No cover art for {Id}: {Message}", track.SourceId, ex.Message);
            return null;
        }
    }

    private async Task<string> RunFfmpegAsync(IEnumerable<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(string.IsNullOrEmpty(_encoder.EncoderPath) ? "ffmpeg" : _encoder.EncoderPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(90));
        var err = proc.StandardError.ReadToEndAsync(cts.Token);
        _ = proc.StandardOutput.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }

        var text = (await err.ConfigureAwait(false)).Trim();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg failed: " + (text.Length > 200 ? text[..200] : text));
        }

        return text;
    }
}
