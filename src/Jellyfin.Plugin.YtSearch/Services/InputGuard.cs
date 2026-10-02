using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Validation for everything that comes from outside: search terms, ids and URLs returned by yt-dlp.</summary>
public static class InputGuard
{
    public const int MaxQueryLength = 200;

    private static readonly Regex YouTubeId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    private static readonly Regex SoundCloudId = new("^[0-9]{1,20}$", RegexOptions.Compiled);

    // Hosts that are allowed to serve artwork / be passed to yt-dlp. Anything else (internal addresses, other sites) is dropped.
    private static readonly string[] ThumbnailHosts = { "ytimg.com", "ggpht.com", "googleusercontent.com", "sndcdn.com", "mzstatic.com" };
    private static readonly string[] PageHosts = { "youtube.com", "soundcloud.com" };

    /// <summary>Ids end up in file names, so only the exact alphabets of each service are accepted (no '/', '..', etc.).</summary>
    public static bool IsValidSourceId(string source, string? id) =>
        id is not null && (source == Sources.SoundCloud ? SoundCloudId.IsMatch(id) : YouTubeId.IsMatch(id));

    /// <summary>Strips control characters and caps the length. Returns empty if nothing usable remains.</summary>
    public static string CleanQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var cleaned = new string(query.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > MaxQueryLength ? cleaned[..MaxQueryLength].Trim() : cleaned;
    }

    /// <summary>Strips control characters from text shown to users and caps its length.</summary>
    public static string CleanText(string? text, int max, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var cleaned = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        return cleaned.Length > max ? cleaned[..max] : cleaned;
    }

    /// <summary>A single folder name from untrusted text: no separators or control characters, never "." or "..", capped length.</summary>
    public static string SafeFolderName(string? text, int max = 100)
    {
        var chars = (text ?? string.Empty).Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? ' ' : c).ToArray();
        var cleaned = Regex.Replace(Regex.Replace(new string(chars), @"\s+", " "), @"\.{2,}", ".").Trim().Trim('.').Trim();
        if (cleaned.Length > max)
        {
            cleaned = cleaned[..max].Trim().Trim('.').Trim();
        }

        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }

    public static string? SafeThumbnailUrl(string? url) => SafeUrl(url, ThumbnailHosts);

    public static string? SafePageUrl(string? url) => SafeUrl(url, PageHosts.Append("api.soundcloud.com").ToArray());

    private static string? SafeUrl(string? url, string[] hosts)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        return hosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)) ? uri.AbsoluteUri : null;
    }
}
