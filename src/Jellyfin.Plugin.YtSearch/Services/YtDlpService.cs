using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

public class DownloadException : Exception
{
    public DownloadException(string message)
        : base(message)
    {
    }
}

public class YtDlpService
{
    private readonly IApplicationPaths _paths;
    private readonly CookieService _cookies;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<YtDlpService> _logger;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    // Bound the number of yt-dlp processes so a burst of requests can't exhaust the server.
    private readonly SemaphoreSlim _lightGate = new(12);
    private readonly SemaphoreSlim _downloadGate = new(3);

    public YtDlpService(IApplicationPaths paths, CookieService cookies, IMediaEncoder encoder, ILogger<YtDlpService> logger)
    {
        _paths = paths;
        _cookies = cookies;
        _encoder = encoder;
        _logger = logger;
    }

    public string DataDirectory => Path.Combine(_paths.DataPath, "ytsearch");

    public async Task<IReadOnlyList<TrackResult>> SearchAsync(string source, string query, int max, CancellationToken ct)
    {
        query = InputGuard.CleanQuery(query);
        if (query.Length == 0)
        {
            return Array.Empty<TrackResult>();
        }

        await _lightGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SearchCoreAsync(source, query, max, ct).ConfigureAwait(false);
        }
        finally
        {
            _lightGate.Release();
        }
    }

    private async Task<IReadOnlyList<TrackResult>> SearchCoreAsync(string source, string query, int max, CancellationToken ct)
    {
        var bin = await EnsureBinaryAsync(ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo(bin)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"{(source == Sources.SoundCloud ? "scsearch" : "ytsearch")}{max}:{query}");
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("--no-warnings");
        if (source == Sources.YouTube)
        {
            // Never send YouTube cookies to other sites.
            AddCookies(psi);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp");
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp exited {proc.ExitCode}: {await stderrTask.ConfigureAwait(false)}");
            }

            return ParseSearchJson(stdout, source);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    public static IReadOnlyList<TrackResult> ParseSearchJson(string json, string source = Sources.YouTube)
    {
        var results = new List<TrackResult>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entries))
        {
            return results;
        }

        foreach (var e in entries.EnumerateArray())
        {
            var id = Str(e, "id");
            // No duration = live/upcoming/not a track.
            if (!InputGuard.IsValidSourceId(source, id) || !e.TryGetProperty("duration", out var d) || d.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var duration = d.GetDouble();
            if (double.IsNaN(duration) || duration <= 0 || duration > 24 * 3600)
            {
                continue;
            }

            var title = InputGuard.CleanText(Str(e, "title"), 300, id!);
            if (source == Sources.SoundCloud)
            {
                var page = InputGuard.SafePageUrl(Str(e, "webpage_url") ?? Str(e, "url"));
                if (page is null)
                {
                    continue;
                }

                var artist = InputGuard.CleanText(Str(e, "uploader") ?? Str(e, "channel"), 200, "SoundCloud");
                results.Add(new TrackResult(source, id!, title, artist, duration, InputGuard.SafeThumbnailUrl(BestThumbnail(e)) ?? string.Empty, page));
            }
            else
            {
                var channel = InputGuard.CleanText(Str(e, "channel") ?? Str(e, "uploader"), 200, "YouTube");
                results.Add(new TrackResult(source, id!, title, channel, duration, $"https://i.ytimg.com/vi/{id}/hqdefault.jpg", $"https://www.youtube.com/watch?v={id}"));
            }
        }

        return results;
    }

    private static string BestThumbnail(JsonElement e)
    {
        if (e.TryGetProperty("thumbnails", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            for (var i = t.GetArrayLength() - 1; i >= 0; i--)
            {
                if (Str(t[i], "url") is { Length: > 0 } url)
                {
                    return url;
                }
            }
        }

        return string.Empty;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Downloads the m4a audio of a track into <paramref name="directory"/> and returns the file path.
    /// Only m4a is accepted (no conversion) so the file is playable and downloadable on iOS.
    /// </summary>
    public async Task<string> DownloadAsync(TrackResult track, string directory, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DownloadCoreAsync(track, directory, ct).ConfigureAwait(false);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private async Task<string> DownloadCoreAsync(TrackResult track, string directory, CancellationToken ct)
    {
        var bin = await EnsureBinaryAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        var psi = new ProcessStartInfo(bin)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-f", "bestaudio[ext=m4a]", "--no-playlist", "--no-warnings", "--no-progress", "-o", Path.Combine(directory, "audio.%(ext)s") })
        {
            psi.ArgumentList.Add(a);
        }

        if (!string.IsNullOrEmpty(_encoder.EncoderPath))
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(_encoder.EncoderPath);
        }

        if (track.Source == Sources.YouTube)
        {
            AddCookies(psi);
        }

        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(track.PageUrl);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }

        await stdout.ConfigureAwait(false);
        var err = await stderr.ConfigureAwait(false);
        var file = Path.Combine(directory, "audio.m4a");
        if (proc.ExitCode != 0 || !File.Exists(file))
        {
            var message = ClassifyDownloadError(err);
            if (message.StartsWith("Download failed", StringComparison.Ordinal))
            {
                // Unrecognised failure: keep the raw yt-dlp output in the server log only.
                _logger.LogWarning("yt-dlp failed for {Source} {Id}: {Error}", track.Source, track.SourceId, err.Trim());
            }

            throw new DownloadException(message);
        }

        return file;
    }

    /// <summary>
    /// Asks yt-dlp whether the m4a download would work, without downloading. True = yes, false = permanently not
    /// (DRM, preview-only, removed), null = inconclusive (network error, timeout), so the caller keeps the track.
    /// </summary>
    public async Task<bool?> CheckDownloadableAsync(TrackResult track, CancellationToken ct)
    {
        try
        {
            await _lightGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            return await CheckCoreAsync(track, ct).ConfigureAwait(false);
        }
        finally
        {
            _lightGate.Release();
        }
    }

    private async Task<bool?> CheckCoreAsync(TrackResult track, CancellationToken ct)
    {
        try
        {
            var bin = await EnsureBinaryAsync(ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo(bin)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-f", "bestaudio[ext=m4a]", "--simulate", "--no-playlist", "--no-warnings", "-q", "--", track.PageUrl })
            {
                psi.ArgumentList.Add(a);
            }

            if (track.Source == Sources.YouTube)
            {
                AddCookies(psi);
            }

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp");
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch (InvalidOperationException) { }
                throw;
            }

            await stdout.ConfigureAwait(false);
            return proc.ExitCode == 0 ? true : IsPermanentFailure(await stderr.ConfigureAwait(false)) ? false : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Playability check failed for {Id}", track.SourceId);
            return null;
        }
    }

    internal static bool IsPermanentFailure(string stderr)
    {
        var l = stderr.ToLowerInvariant();
        return l.Contains("drm") || l.Contains("requested format is not available") || l.Contains("private video")
            || l.Contains("video unavailable") || l.Contains("has been removed") || l.Contains("does not exist") || l.Contains("404");
    }

    internal static string ClassifyDownloadError(string stderr)
    {
        var l = stderr.ToLowerInvariant();
        if (l.Contains("drm"))
        {
            return "This track is DRM-protected and can't be downloaded.";
        }

        if (l.Contains("confirm your age") || l.Contains("age-restricted") || l.Contains("age restricted"))
        {
            return "This video is age-restricted. Upload YouTube cookies in the plugin settings.";
        }

        if (l.Contains("not a bot") || l.Contains("sign in to confirm"))
        {
            return "YouTube is asking to confirm you're not a bot. Upload fresh YouTube cookies in the plugin settings.";
        }

        if (l.Contains("country") || l.Contains("geo") || l.Contains("not available in your"))
        {
            return "This track is blocked in your region.";
        }

        if (l.Contains("private video") || l.Contains("video unavailable") || l.Contains("has been removed") || l.Contains("been terminated") || l.Contains("does not exist") || l.Contains("404"))
        {
            return "This track is unavailable (deleted or private).";
        }

        if (l.Contains("requested format is not available"))
        {
            return "No m4a audio stream is available for this track.";
        }

        return "Download failed. See the Jellyfin log for details.";
    }

    /// <summary>Adds --cookies when a cookie file is available. Use for every yt-dlp call that talks to YouTube.</summary>
    public void AddCookies(ProcessStartInfo psi)
    {
        if (_cookies.ActivePath is { } path)
        {
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(path);
        }
    }

    /// <summary>
    /// Verifies the cookies by reading the account's Liked videos playlist, which only works when signed in.
    /// </summary>
    public async Task<CheckResult> CheckCookiesAsync(CancellationToken ct)
    {
        CheckResult result;
        if (_cookies.ActivePath is null)
        {
            result = new CheckResult { State = "none", Message = "No cookies uploaded." };
        }
        else
        {
            result = await RunCheckAsync(ct).ConfigureAwait(false);
        }

        result.CheckedAt = DateTime.UtcNow;
        _cookies.SetLastCheck(result);
        return result;
    }

    private async Task<CheckResult> RunCheckAsync(CancellationToken ct)
    {
        try
        {
            var bin = await EnsureBinaryAsync(ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo(bin)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            AddCookies(psi);
            foreach (var a in new[] { "--flat-playlist", "--playlist-items", "1", "--no-warnings", "-J", "https://www.youtube.com/playlist?list=LL" })
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch (InvalidOperationException) { }
                return new CheckResult { State = "error", Message = "Check timed out. YouTube did not respond in 45 seconds." };
            }

            await stdout.ConfigureAwait(false);
            var err = await stderr.ConfigureAwait(false);
            if (proc.ExitCode == 0)
            {
                return new CheckResult { State = "working", Message = "Cookies are working. YouTube sees you as signed in." };
            }

            _logger.LogWarning("Cookie check failed: {Error}", err.Trim().Length > 500 ? err.Trim()[..500] : err.Trim());
            return ClassifyFailure(err);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cookie check failed");
            return new CheckResult { State = "error", Message = "Could not run the check: " + ex.Message };
        }
    }

    internal static CheckResult ClassifyFailure(string stderr)
    {
        var lower = stderr.ToLowerInvariant();
        if (lower.Contains("no longer valid") || lower.Contains("rotated"))
        {
            return new CheckResult { State = "failing", Message = "YouTube has rotated or invalidated these cookies. Export a fresh cookies.txt from a private window and upload it again." };
        }

        if (lower.Contains("sign in") || lower.Contains("log in") || lower.Contains("login") || lower.Contains("does not exist") || lower.Contains("private") || lower.Contains("cookies"))
        {
            return new CheckResult { State = "failing", Message = "YouTube did not accept the cookies (not signed in or expired). Upload a fresh cookies.txt." };
        }

        // Raw output can quote lines of whatever file CookiesPath points at, so it is not shown in the UI.
        return new CheckResult { State = "error", Message = "yt-dlp could not run the check. See the Jellyfin log for details." };
    }

    /// <summary>Runs yt-dlp's built-in updater on the configured channel (nightly by default).</summary>
    public async Task UpdateAsync(CancellationToken ct)
    {
        var bin = await EnsureBinaryAsync(ct).ConfigureAwait(false);
        var channel = Plugin.Instance?.Configuration.UpdateChannel is { Length: > 0 } c ? c : "nightly";
        var psi = new ProcessStartInfo(bin)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--update-to");
        psi.ArgumentList.Add($"{channel}@latest");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start yt-dlp");
        var err = proc.StandardError.ReadToEndAsync(ct);
        var output = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            _logger.LogWarning("yt-dlp update failed ({Code}): {Err}", proc.ExitCode, await err.ConfigureAwait(false));
            return;
        }

        _logger.LogInformation("yt-dlp update ({Channel}): {Output}", channel, output.Trim().ReplaceLineEndings(" | "));
    }

    /// <summary>Fails closed: the binary is only kept if it matches the SHA2-256SUMS published with the release.</summary>
    private static async Task VerifyChecksumAsync(HttpClient http, string asset, string file, CancellationToken ct)
    {
        try
        {
            var sums = await http.GetStringAsync("https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/SHA2-256SUMS", ct).ConfigureAwait(false);
            var expected = sums.Split('\n')
                .Select(l => l.Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1] == asset)?[0];
            await using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (expected is null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("yt-dlp download failed checksum verification");
            }
        }
        catch
        {
            File.Delete(file);
            throw;
        }
    }

    private async Task<string> EnsureBinaryAsync(CancellationToken ct)
    {
        var configured = Plugin.Instance?.Configuration.YtDlpPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var target = Path.Combine(DataDirectory, "yt-dlp");
        if (File.Exists(target))
        {
            return target;
        }

        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }

            Directory.CreateDirectory(DataDirectory);
            var asset = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
            _logger.LogInformation("Downloading {Asset} to {Target}", asset, target);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var tmp = target + ".tmp";
            await using (var src = await http.GetStreamAsync($"https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/{asset}", ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }

            await VerifyChecksumAsync(http, asset, tmp, ct).ConfigureAwait(false);
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(tmp, target, true);
            return target;
        }
        finally
        {
            _installLock.Release();
        }
    }
}
