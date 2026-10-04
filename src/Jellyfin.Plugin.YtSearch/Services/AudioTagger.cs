using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
            var cover = await PrepareCoverAsync(track, workDir, ct).ConfigureAwait(false);
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

            args.AddRange(new[] { "-c", "copy", "-map_metadata", "-1", "-movflags", "+faststart" });
            foreach (var (key, value) in Tags(track))
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

    private async Task RunFfmpegAsync(IEnumerable<string> args, CancellationToken ct)
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
        cts.CancelAfter(TimeSpan.FromSeconds(90));
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

        if (proc.ExitCode != 0)
        {
            var text = (await err.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException("ffmpeg failed: " + (text.Length > 200 ? text[..200] : text));
        }
    }
}
