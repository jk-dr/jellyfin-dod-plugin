using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private readonly ILogger<YtDlpService> _logger;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public YtDlpService(IApplicationPaths paths, ILogger<YtDlpService> logger)
    {
        _paths = paths;
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
            await using (var src = await http.GetStreamAsync($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{asset}", ct).ConfigureAwait(false))
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
