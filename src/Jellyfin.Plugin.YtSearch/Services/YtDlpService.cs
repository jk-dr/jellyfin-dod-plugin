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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

public class YtDlpService
{
    private readonly IApplicationPaths _paths;
    private readonly CookieService _cookies;
    private readonly ILogger<YtDlpService> _logger;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public YtDlpService(IApplicationPaths paths, CookieService cookies, ILogger<YtDlpService> logger)
    {
        _paths = paths;
        _cookies = cookies;
        _logger = logger;
    }

    public string DataDirectory => Path.Combine(_paths.DataPath, "ytsearch");

    public async Task<IReadOnlyList<YtResult>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var bin = await EnsureBinaryAsync(ct).ConfigureAwait(false);
        var psi = new ProcessStartInfo(bin)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"ytsearch{max}:{query}");
        psi.ArgumentList.Add("--flat-playlist");
        psi.ArgumentList.Add("-J");
        psi.ArgumentList.Add("--no-warnings");
        AddCookies(psi);

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

            return ParseSearchJson(stdout);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    public static IReadOnlyList<YtResult> ParseSearchJson(string json)
    {
        var results = new List<YtResult>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entries", out var entries))
        {
            return results;
        }

        foreach (var e in entries.EnumerateArray())
        {
            var id = Str(e, "id");
            // 11-char ids are videos; skips channels/playlists. No duration = live/upcoming.
            if (id is not { Length: 11 } || !e.TryGetProperty("duration", out var d) || d.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var title = Str(e, "title") ?? id;
            var channel = Str(e, "channel") ?? Str(e, "uploader") ?? "YouTube";
            results.Add(new YtResult(id, title, channel, d.GetDouble(), $"https://i.ytimg.com/vi/{id}/hqdefault.jpg"));
        }

        return results;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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

        var last = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "unknown error";
        return new CheckResult { State = "error", Message = "yt-dlp failed: " + (last.Length > 300 ? last[..300] : last) };
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
