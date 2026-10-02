using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.YtSearch.Services;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Middleware;

/// <summary>
/// 1. Appends YouTube/SoundCloud results to /Items?searchTerm= and /Search/Hints responses.
/// 2. Serves metadata and thumbnails for results that are not in the library yet.
/// 3. The first time a client plays, downloads, favorites or playlists a result, downloads it and
///    adds it to the library, then lets the original request through to Jellyfin.
/// Touches only request/response JSON, no Jellyfin controllers.
/// </summary>
public class SearchAppendMiddleware
{
    private const string GuidToken = @"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}";

    private static readonly Regex ItemsPath = new(@"(^|/)(Users/[^/]+/)?Items/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HintsPath = new(@"(^|/)Search/Hints/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImagePath = new($@"(^|/)Items/({GuidToken})/Images/(Primary|Thumb)(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItemPath = new($@"(^|/)(Users/[^/]+/)?Items/({GuidToken})/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NeedsFilePath = new(@"(^|/)(Audio/|Items/[^/]+/(Download|File|PlaybackInfo)(/|$)|Users/[^/]+/FavoriteItems/|UserFavoriteItems/|Playlists(/|$))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GuidRegex = new(GuidToken, RegexOptions.Compiled);

    private readonly RequestDelegate _next;
    private readonly SearchService _search;
    private readonly DownloadService _downloads;
    private readonly LibraryService _library;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<SearchAppendMiddleware> _logger;

    public SearchAppendMiddleware(
        RequestDelegate next,
        SearchService search,
        DownloadService downloads,
        LibraryService library,
        IServerApplicationHost host,
        ILogger<SearchAppendMiddleware> logger)
    {
        _next = next;
        _search = search;
        _downloads = downloads;
        _library = library;
        _host = host;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        var path = req.Path.Value ?? string.Empty;

        // Fast exit for the vast majority of requests.
        if (!path.Contains("Items", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("Audio", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("Search", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("Playlists", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        if (HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method))
        {
            var img = ImagePath.Match(path);
            if (img.Success && Guid.TryParse(img.Groups[2].Value, out var imgId) && _search.TryResolve(imgId, out var imgResult)
                && imgResult.ThumbnailUrl.Length > 0 && !_library.IsPromoted(imgResult.TrackId))
            {
                ctx.Response.Redirect(imgResult.ThumbnailUrl);
                return;
            }

            var item = ItemPath.Match(path);
            if (item.Success && Guid.TryParse(item.Groups[3].Value, out var itemId) && _search.TryResolve(itemId, out var itemResult)
                && !_library.IsPromoted(itemResult.TrackId))
            {
                await WriteJsonAsync(ctx, Item(itemResult, _host.SystemId).ToJsonString());
                return;
            }
        }

        if (NeedsFilePath.IsMatch(path) && !await EnsureDownloadedAsync(ctx, path))
        {
            return;
        }

        if (HttpMethods.IsGet(req.Method))
        {
            var isItems = ItemsPath.IsMatch(path);
            var isHints = !isItems && HintsPath.IsMatch(path);
            var term = req.Query["searchTerm"].ToString().Trim();
            if ((isItems || isHints) && term.Length >= 2 && WantsAudio(req)
                && !(int.TryParse(req.Query["startIndex"], out var start) && start > 0))
            {
                await AppendSearchAsync(ctx, path, term, isHints);
                return;
            }
        }

        await _next(ctx);
    }

    /// <summary>Downloads every not-yet-downloaded result this request refers to. Returns false if it answered with an error.</summary>
    private async Task<bool> EnsureDownloadedAsync(HttpContext ctx, string path)
    {
        var req = ctx.Request;
        var ids = new HashSet<Guid>();
        void Collect(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match m in GuidRegex.Matches(text))
            {
                if (Guid.TryParse(m.Value, out var g))
                {
                    ids.Add(g);
                }
            }
        }

        Collect(path);
        foreach (var key in new[] { "ids", "Ids", "itemIds", "ItemIds" })
        {
            Collect(req.Query[key].ToString());
        }

        if (HttpMethods.IsPost(req.Method) && path.TrimEnd('/').EndsWith("Playlists", StringComparison.OrdinalIgnoreCase)
            && (req.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            req.EnableBuffering();
            using (var reader = new StreamReader(req.Body, leaveOpen: true))
            {
                Collect(await reader.ReadToEndAsync());
            }

            req.Body.Position = 0;
        }

        var pending = new List<TrackResult>();
        foreach (var id in ids)
        {
            if (_search.TryResolve(id, out var track) && id == track.TrackId && !_library.IsPromoted(id))
            {
                pending.Add(track);
            }
        }

        if (pending.Count == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(pending.Select(t => _downloads.EnsureAsync(t, ctx.RequestAborted)));
            return true;
        }
        catch (DownloadException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await WriteJsonAsync(ctx, new JsonObject { ["title"] = "Track unavailable", ["detail"] = ex.Message, ["status"] = 404 }.ToJsonString(), 404);
            return false;
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task AppendSearchAsync(HttpContext ctx, string path, string term, bool isHints)
    {
        var req = ctx.Request;
        if (Plugin.Instance?.Configuration.LogSearchRequests == true)
        {
            _logger.LogInformation("Search request {Path}{Query}", path, req.QueryString);
        }

        // Start the searches now so they run in parallel with Jellyfin's own search.
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

    private static async Task WriteJsonAsync(HttpContext ctx, string json, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
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

        // Tracks already in the library come back from Jellyfin itself; don't list them twice.
        var present = arr
            .Select(n => n?["Id"]?.GetValue<string>() ?? n?["ItemId"]?.GetValue<string>())
            .Where(i => i is not null && Guid.TryParse(i, out _))
            .Select(i => Guid.Parse(i!))
            .ToHashSet();
        var fresh = results.Where(r => !present.Contains(r.TrackId)).ToList();
        if (fresh.Count == 0)
        {
            return null;
        }

        var existing = arr.Count;
        foreach (var r in fresh)
        {
            arr.Add(hints ? Hint(r, term) : Item(r, serverId));
        }

        root["TotalRecordCount"] = (root["TotalRecordCount"]?.GetValue<int>() ?? existing) + fresh.Count;
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string N(Guid g) => g.ToString("N");

    internal static JsonObject Item(TrackResult r, string serverId) => new()
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
