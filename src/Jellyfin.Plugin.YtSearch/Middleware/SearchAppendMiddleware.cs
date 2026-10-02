using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.YtSearch.Services;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Middleware;

/// <summary>
/// Appends YouTube results to Jellyfin's /Items?searchTerm= and /Search/Hints responses
/// and serves thumbnails for the virtual items. Touches only the JSON, no Jellyfin internals.
/// </summary>
public class SearchAppendMiddleware
{
    private static readonly Regex ItemsPath = new(@"(^|/)(Users/[^/]+/)?Items/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HintsPath = new(@"(^|/)Search/Hints/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImagePath = new(@"(^|/)Items/([0-9a-fA-F-]{32,36})/Images/(Primary|Thumb)(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly RequestDelegate _next;
    private readonly SearchService _search;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<SearchAppendMiddleware> _logger;

    public SearchAppendMiddleware(RequestDelegate next, SearchService search, IServerApplicationHost host, ILogger<SearchAppendMiddleware> logger)
    {
        _next = next;
        _search = search;
        _host = host;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        if (!HttpMethods.IsGet(req.Method))
        {
            await _next(ctx);
            return;
        }

        var path = req.Path.Value ?? string.Empty;

        var img = ImagePath.Match(path);
        if (img.Success && Guid.TryParse(img.Groups[2].Value, out var imgId) && _search.TryResolve(imgId, out var imgResult) && imgResult.ThumbnailUrl.Length > 0)
        {
            ctx.Response.Redirect(imgResult.ThumbnailUrl);
            return;
        }

        var isItems = ItemsPath.IsMatch(path);
        var isHints = !isItems && HintsPath.IsMatch(path);
        var term = req.Query["searchTerm"].ToString().Trim();
        if ((!isItems && !isHints) || term.Length < 2 || !WantsAudio(req) || (int.TryParse(req.Query["startIndex"], out var start) && start > 0))
        {
            await _next(ctx);
            return;
        }

        if (Plugin.Instance?.Configuration.LogSearchRequests == true)
        {
            _logger.LogInformation("Search request {Path}{Query}", path, req.QueryString);
        }

        // Start the YouTube search now so it runs in parallel with Jellyfin's own search.
        var ytTask = _search.SearchAsync(term, ctx.RequestAborted);

        // Prevent compressed responses so the body can be edited.
        req.Headers.Remove("Accept-Encoding");
        var original = ctx.Response.Body;
        await using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await _next(ctx);
        }
        finally
        {
            ctx.Response.Body = original;
        }

        var bytes = buffer.ToArray();
        if (ctx.Response.StatusCode == 200 && (ctx.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            try
            {
                var yt = await ytTask.ConfigureAwait(false);
                var edited = Append(bytes, yt, isHints, term, _host.SystemId);
                if (edited is not null)
                {
                    bytes = edited;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "YouTube search failed; returning Jellyfin results only");
            }
        }

        ctx.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, ctx.RequestAborted);
    }

    private static bool WantsAudio(HttpRequest req)
    {
        var types = req.Query["includeItemTypes"].SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
        return types.Count == 0 || types.Contains("Audio", StringComparer.OrdinalIgnoreCase);
    }

    internal static byte[]? Append(byte[] body, IReadOnlyList<TrackResult> results, bool hints, string term, string serverId)
    {
        if (results.Count == 0 || JsonNode.Parse(body) is not JsonObject root)
        {
            return null;
        }

        var key = hints ? "SearchHints" : "Items";
        if (root[key] is not JsonArray arr)
        {
            return null;
        }

        var existing = arr.Count;
        foreach (var r in results)
        {
            arr.Add(hints ? Hint(r, term) : Item(r, serverId));
        }

        root["TotalRecordCount"] = (root["TotalRecordCount"]?.GetValue<int>() ?? existing) + results.Count;
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string N(Guid g) => g.ToString("N");

    private static JsonObject Item(TrackResult r, string serverId) => new()
    {
        ["Name"] = r.Title,
        ["ServerId"] = serverId,
        ["Id"] = N(r.TrackId),
        ["CanDelete"] = false,
        ["CanDownload"] = true,
        ["RunTimeTicks"] = r.RunTimeTicks,
        ["ProductionYear"] = null,
        ["IsFolder"] = false,
        ["Type"] = "Audio",
        ["MediaType"] = "Audio",
        ["Artists"] = new JsonArray(r.Artist),
        ["ArtistItems"] = new JsonArray(new JsonObject { ["Name"] = r.Artist, ["Id"] = N(r.ArtistId) }),
        ["Album"] = r.Title,
        ["AlbumId"] = N(r.AlbumId),
        ["AlbumArtist"] = r.Artist,
        ["AlbumArtists"] = new JsonArray(new JsonObject { ["Name"] = r.Artist, ["Id"] = N(r.ArtistId) }),
        ["ImageTags"] = new JsonObject { ["Primary"] = r.ImageTag },
        ["BackdropImageTags"] = new JsonArray(),
        ["AlbumPrimaryImageTag"] = r.ImageTag,
        ["LocationType"] = "FileSystem",
        ["UserData"] = new JsonObject { ["PlaybackPositionTicks"] = 0, ["PlayCount"] = 0, ["IsFavorite"] = false, ["Played"] = false, ["Key"] = N(r.TrackId) },
    };

    private static JsonObject Hint(TrackResult r, string term) => new()
    {
        ["ItemId"] = N(r.TrackId),
        ["Id"] = N(r.TrackId),
        ["Name"] = r.Title,
        ["MatchedTerm"] = term,
        ["PrimaryImageTag"] = r.ImageTag,
        ["Type"] = "Audio",
        ["IsFolder"] = false,
        ["RunTimeTicks"] = r.RunTimeTicks,
        ["MediaType"] = "Audio",
        ["Album"] = r.Title,
        ["AlbumId"] = N(r.AlbumId),
        ["AlbumArtist"] = r.Artist,
        ["Artists"] = new JsonArray(r.Artist),
    };
}
