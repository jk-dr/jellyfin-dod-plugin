using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.YtSearch.Services;

public record CookieEntry(string Domain, string Name, long Expiry);

public class CheckResult
{
    /// <summary>working | failing | error | none</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "none";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("checkedAt")]
    public DateTime? CheckedAt { get; set; }
}

public class CookieStatus
{
    [JsonPropertyName("present")]
    public bool Present { get; set; }

    /// <summary>uploaded | path | none</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "none";

    [JsonPropertyName("cookieCount")]
    public int CookieCount { get; set; }

    [JsonPropertyName("loginCookiesFound")]
    public bool LoginCookiesFound { get; set; }

    [JsonPropertyName("loginCookiesExpire")]
    public DateTime? LoginCookiesExpire { get; set; }

    [JsonPropertyName("loginCookiesExpired")]
    public bool LoginCookiesExpired { get; set; }

    [JsonPropertyName("uploadedAt")]
    public DateTime? UploadedAt { get; set; }

    [JsonPropertyName("lastCheck")]
    public CheckResult? LastCheck { get; set; }
}

/// <summary>Stores the user's YouTube cookies.txt and reports on it. Never logs cookie contents.</summary>
public class CookieService
{
    private static readonly HashSet<string> LoginCookieNames = new(StringComparer.Ordinal)
    {
        "SID", "SAPISID", "__Secure-1PSID", "__Secure-3PSID", "LOGIN_INFO",
    };

    private readonly IApplicationPaths _paths;
    private readonly object _lock = new();
    private CheckResult? _lastCheck;

    public CookieService(IApplicationPaths paths)
    {
        _paths = paths;
    }

    public string UploadedPath => Path.Combine(_paths.DataPath, "ytsearch", "cookies.txt");

    /// <summary>The cookie file yt-dlp should use, or null.</summary>
    public string? ActivePath
    {
        get
        {
            if (File.Exists(UploadedPath))
            {
                return UploadedPath;
            }

            var configured = Plugin.Instance?.Configuration.CookiesPath;
            return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : null;
        }
    }

    /// <summary>Validates and stores pasted/uploaded cookies. Throws ArgumentException with a user-facing message.</summary>
    public void Save(string text)
    {
        var normalized = Normalize(text);
        var cookies = Parse(normalized);
        if (cookies.Count == 0)
        {
            throw new ArgumentException("No cookies found. The file must be in Netscape cookies.txt format (tab-separated, 7 fields per line).");
        }

        if (!cookies.Any(c => IsYoutubeDomain(c.Domain)))
        {
            throw new ArgumentException("No youtube.com or google.com cookies found in this file.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(UploadedPath)!);
        var tmp = UploadedPath + ".tmp";
        File.WriteAllText(tmp, normalized, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, UploadedPath, true);
        SetLastCheck(null);
    }

    public void Delete()
    {
        if (File.Exists(UploadedPath))
        {
            File.Delete(UploadedPath);
        }

        SetLastCheck(null);
    }

    public CheckResult? LastCheck
    {
        get { lock (_lock) { return _lastCheck; } }
    }

    public void SetLastCheck(CheckResult? result)
    {
        lock (_lock) { _lastCheck = result; }
    }

    public CookieStatus GetStatus()
    {
        var status = new CookieStatus { LastCheck = LastCheck };
        var path = ActivePath;
        if (path is null)
        {
            return status;
        }

        status.Present = true;
        status.Source = path == UploadedPath ? "uploaded" : "path";
        if (status.Source == "uploaded")
        {
            status.UploadedAt = File.GetLastWriteTimeUtc(path);
        }

        List<CookieEntry> cookies;
        try
        {
            cookies = Parse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return status;
        }

        status.CookieCount = cookies.Count;
        var login = cookies.Where(c => IsYoutubeDomain(c.Domain) && LoginCookieNames.Contains(c.Name)).ToList();
        status.LoginCookiesFound = login.Count > 0;
        var expiring = login.Where(c => c.Expiry > 0).Select(c => c.Expiry).ToList();
        if (expiring.Count > 0)
        {
            var min = DateTimeOffset.FromUnixTimeSeconds(expiring.Min()).UtcDateTime;
            status.LoginCookiesExpire = min;
            status.LoginCookiesExpired = min < DateTime.UtcNow;
        }

        return status;
    }

    public static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('﻿', '\n', ' ') + "\n";

    public static List<CookieEntry> Parse(string text)
    {
        var list = new List<CookieEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                line = line["#HttpOnly_".Length..];
            }
            else if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var f = line.Split('\t');
            if (f.Length != 7)
            {
                continue;
            }

            long.TryParse(f[4], out var expiry);
            list.Add(new CookieEntry(f[0].TrimStart('.'), f[5], expiry));
        }

        return list;
    }

    private static bool IsYoutubeDomain(string domain) =>
        domain.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || domain.EndsWith("google.com", StringComparison.OrdinalIgnoreCase);
}
