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
using MediaBrowser.Controller.Net;
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
    private static readonly Regex ArtistsPath = new(@"(^|/)Artists(/AlbumArtists)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HintsPath = new(@"(^|/)Search/Hints/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImagePath = new($@"(^|/)Items/({GuidToken})/Images/(Primary|Thumb)(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ItemPath = new($@"(^|/)(Users/[^/]+/)?Items/({GuidToken})/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NeedsFilePath = new(@"(^|/)(Audio/|Items/[^/]+/(Download|File|PlaybackInfo)(/|$)|Users/[^/]+/FavoriteItems/|UserFavoriteItems/|Playlists(/|$))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GuidRegex = new(GuidToken, RegexOptions.Compiled);

    private readonly RequestDelegate _next;
    private readonly SearchService _search;
    private readonly DownloadService _downloads;
    private readonly LibraryService _library;
    private readonly ArtistProfileService _artists;
    private readonly IServerApplicationHost _host;
    private readonly IAuthorizationContext _auth;
    private readonly ILogger<SearchAppendMiddleware> _logger;

    public SearchAppendMiddleware(
        RequestDelegate next,
        SearchService search,
        DownloadService downloads,
        LibraryService library,
        ArtistProfileService artists,
        IServerApplicationHost host,
        IAuthorizationContext auth,
        ILogger<SearchAppendMiddleware> logger)
    {
        _auth = auth;
        _next = next;
        _search = search;
        _downloads = downloads;
        _library = library;
        _artists = artists;
        _host = host;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        var path = req.Path.Value ?? string.Empty;

        if (Plugin.Instance?.Configuration.LogAllApiRequests == true && !path.Contains("/Images/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("API {Method} {Path} client='{Client}'", req.Method, path, ClientName(req));
        }

        // Search requests are always logged (term and types only; the raw query string can hold tokens),
        // including ones this plugin ignores, so it is visible what an app really sends.
        if (Plugin.Instance?.Configuration.LogSearchRequests == true && req.Query.ContainsKey("searchTerm")
            && (ItemsPath.IsMatch(path) || HintsPath.IsMatch(path) || ArtistsPath.IsMatch(path)))
        {
            _logger.LogInformation(
                "Search seen: {Method} {Path} term='{Term}' types='{Types}' parentId='{Parent}' client='{Client}'",
                req.Method,
                path,
                InputGuard.CleanQuery(req.Query["searchTerm"].ToString()),
                req.Query["includeItemTypes"].ToString(),
                req.Query["parentId"].ToString(),
                ClientName(req));
        }

        // Fast exit for the vast majority of requests.
        if (!path.Contains("Items", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("Audio", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("Search", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("Artists", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("Playlists", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        if (HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method))
        {
            var img = ImagePath.Match(path);
            TrackResult? imgResult = null;
            if (img.Success && Guid.TryParse(img.Groups[2].Value, out var imgId))
            {
                // A track or album of a search result, or one of its artists (shown with the track's cover).
                imgResult = _search.TryResolve(imgId, out var resolved) ? resolved : _search.FindArtist(imgId)?.Track;
            }

            if (imgResult is not null && InputGuard.SafeThumbnailUrl(imgResult.ThumbnailUrl) is { } safeThumb && !_library.IsPromoted(imgResult.TrackId))
            {
                // No login check, on purpose: Jellyfin serves artwork anonymously and apps fetch images without credentials.
                // This only redirects to a public cover image of an allowlisted host, for ids we handed out ourselves.
                ctx.Response.Redirect(safeThumb);
                return;
            }

            // The album of a search result that is not in the library yet: an album page listing the known songs of it.
            var albumItem = ItemPath.Match(path);
            if (albumItem.Success && Guid.TryParse(albumItem.Groups[3].Value, out var albumId) && _search.TryResolve(albumId, out var albumTrack)
                && albumTrack.Meta is not null && albumId == albumTrack.AlbumId && _library.GetItem(albumId) is null)
            {
                if (!await IsAllowedAsync(ctx))
                {
                    await _next(ctx);
                    return;
                }

                await WriteJsonAsync(ctx, Album(albumTrack, _host.SystemId, await TracksOfVirtualAlbumAsync(albumTrack, albumId, ctx.RequestAborted)).ToJsonString());
                return;
            }

            if (ItemsPath.IsMatch(path) && Guid.TryParse(req.Query["parentId"].ToString(), out var parentAlbum)
                && _search.TryResolve(parentAlbum, out var parentTrack) && parentTrack.Meta is not null && parentAlbum == parentTrack.AlbumId
                && _library.GetItem(parentAlbum) is null)
            {
                if (!await IsAllowedAsync(ctx))
                {
                    await _next(ctx);
                    return;
                }

                var children = new JsonArray();
                foreach (var t in await TracksOfVirtualAlbumAsync(parentTrack, parentAlbum, ctx.RequestAborted))
                {
                    children.Add(Item(t, _host.SystemId));
                }

                await WriteJsonAsync(ctx, new JsonObject { ["Items"] = children, ["TotalRecordCount"] = children.Count, ["StartIndex"] = 0 }.ToJsonString());
                return;
            }

            var artistItem = ItemPath.Match(path);
            if (artistItem.Success && Guid.TryParse(artistItem.Groups[3].Value, out var artistPageId) && !_search.TryResolve(artistPageId, out _)
                && _library.GetItem(artistPageId) is null && _search.FindArtist(artistPageId) is { } foundArtist)
            {
                if (!await IsAllowedAsync(ctx))
                {
                    await _next(ctx);
                    return;
                }

                await WriteJsonAsync(ctx, Artist(foundArtist.Track, foundArtist.Name, _host.SystemId).ToJsonString());
                return;
            }

            var item = ItemPath.Match(path);
            if (item.Success && Guid.TryParse(item.Groups[3].Value, out var itemId) && _search.TryResolve(itemId, out var itemResult)
                && itemId == itemResult.TrackId && !_library.IsPromoted(itemResult.TrackId))
            {
                if (!await IsAllowedAsync(ctx))
                {
                    await _next(ctx);
                    return;
                }

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
            var isArtistsPath = !isItems && ArtistsPath.IsMatch(path);
            var isHints = !isItems && !isArtistsPath && HintsPath.IsMatch(path);
            var term = InputGuard.CleanQuery(req.Query["searchTerm"].ToString());
            var firstPage = !(int.TryParse(req.Query["startIndex"], out var start) && start > 0);

            // Apps search each kind separately (songs, albums, artists): fill in whichever kinds this request asks for.
            var songs = (isItems || isHints) && WantsAudio(req);
            var albums = (isItems || isHints) && WantsAlbums(req);
            var artists = isArtistsPath || ((isItems || isHints) && WantsArtists(req));
            if ((isItems || isHints || isArtistsPath) && term.Length >= 2 && firstPage && (songs || albums || artists) && await IsAllowedAsync(ctx))
            {
                // Start the searches now so they run in parallel with Jellyfin's own search.
                var found = _search.SearchAsync(term, ctx.RequestAborted);
                await AppendOnlineAsync(
                    ctx,
                    async body =>
                    {
                        var results = await found.ConfigureAwait(false);
                        var current = body;
                        if (songs)
                        {
                            current = Append(current, results, isHints, term, _host.SystemId) ?? current;
                        }

                        if (albums)
                        {
                            var reps = await AlbumsForSearchAsync(term, results, ctx.RequestAborted).ConfigureAwait(false);
                            current = AppendAlbums(current, reps, isHints, term, _host.SystemId, _search.TracksOfAlbum) ?? current;
                        }

                        if (artists)
                        {
                            var named = ArtistsForSearch(term, results);
                            if (named.Count == 0 && await _artists.ArtistNamedAsync(term, results, ctx.RequestAborted).ConfigureAwait(false) is { } catalogArtist)
                            {
                                // Known to the catalog but not among the results: stand the artist up with one of their albums.
                                var placement = await _artists.ForArtistAsync(catalogArtist, _library.ArtistIdByName(catalogArtist), SearchProfileBudget, ctx.RequestAborted).ConfigureAwait(false);
                                if (placement?.AlbumRepresentatives.FirstOrDefault() is { } rep)
                                {
                                    named = new[] { (rep, catalogArtist) };
                                }
                            }

                            if (named.Count == 0 && await _artists.TemporaryArtistAsync(term, SearchProfileBudget, ctx.RequestAborted).ConfigureAwait(false) is { } temporary)
                            {
                                // Not in the catalog or the song results, but the term is the name of a channel or profile: a temporary artist.
                                named = new[] { (temporary.Track, temporary.Name) };
                            }

                            current = AppendArtists(current, named, isHints, term, _host.SystemId) ?? current;
                        }

                        return ReferenceEquals(current, body) ? null : current;
                    },
                    $"Search '{term}'");
                return;
            }

            // An app that syncs the whole library (Manet): put the recent results in front of the lists it copies.
            if ((isItems || isArtistsPath) && term.Length == 0 && Plugin.Instance?.Configuration.SyncRecentDays > 0
                && Guid.TryParse(req.Query["parentId"].ToString(), out var syncParent) && syncParent == _library.MusicLibraryId
                && ArtistIdOf(req) is null && await IsSyncClientAsync(ctx))
            {
                var injected = SyncNodes(req, isArtistsPath);
                if (injected.Count > 0)
                {
                    int.TryParse(req.Query["startIndex"], out var syncStart);
                    int? syncLimit = int.TryParse(req.Query["limit"], out var parsedLimit) ? parsedLimit : null;
                    var (fromInjected, realStart, realLimit) = SyncInjection.Plan(syncStart, syncLimit, injected.Count);
                    var page = injected.Skip(Math.Max(0, syncStart)).Take(fromInjected).ToList();
                    var query = req.Query.Where(kv => !kv.Key.Equals("startIndex", StringComparison.OrdinalIgnoreCase) && !kv.Key.Equals("limit", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v)))
                        .Append(new KeyValuePair<string, string?>("startIndex", realStart.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    if (realLimit is { } rl)
                    {
                        query = query.Append(new KeyValuePair<string, string?>("limit", Math.Max(1, rl).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }

                    req.QueryString = QueryString.Create(query);
                    await AppendOnlineAsync(ctx, body => Task.FromResult(SyncInjection.Merge(body, page, injected.Count, syncStart, realLimit)), $"Library sync ({injected.Count} recent results)");
                    return;
                }
            }

            if (isItems && term.Length < 2 && firstPage)
            {
                var artistId = ArtistIdOf(req);

                // Opening an artist: its songs and albums from its YouTube and SoundCloud profiles that the library lacks.
                if (artistId is { } id && (WantsAudio(req) || WantsAlbums(req)))
                {
                    var artistName = _library.ArtistName(id) ?? _search.FindArtist(id)?.Name;
                    if (!string.IsNullOrEmpty(artistName) && await IsAllowedAsync(ctx))
                    {
                        if (WantsAlbums(req) && !WantsAudio(req))
                        {
                            await AppendOnlineAsync(ctx, async body => AppendAlbums(body, await ArtistAlbumsAsync(artistName, id, ctx.RequestAborted), false, artistName, _host.SystemId, _search.TracksOfAlbum), $"Albums of '{artistName}'");
                            return;
                        }

                        if (WantsAudio(req))
                        {
                            await AppendOnlineAsync(ctx, async body => Append(body, await ArtistSongsAsync(artistName, id, ctx.RequestAborted), false, artistName, _host.SystemId), $"Artist '{artistName}'");
                            return;
                        }
                    }
                }

                // Opening an album the library has: the songs of it that are missing.
                if (artistId is null && WantsAudio(req) && Guid.TryParse(req.Query["parentId"].ToString(), out var albumParent)
                    && _library.AlbumContents(albumParent) is not null && await IsAllowedAsync(ctx))
                {
                    await AppendOnlineAsync(ctx, async body => Append(body, await _artists.MissingForAlbumAsync(albumParent, ProfileBudget, ctx.RequestAborted), false, string.Empty, _host.SystemId), "Album gaps");
                    return;
                }
            }
        }

        await _next(ctx);
    }

    /// <summary>Whether the caller is a signed-in, allowed user of an app that copies the library (see the SyncClients setting).</summary>
    private async Task<bool> IsSyncClientAsync(HttpContext ctx)
    {
        var names = (Plugin.Instance?.Configuration.SyncClients ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            return false;
        }

        try
        {
            var info = await _auth.GetAuthorizationInfo(ctx).ConfigureAwait(false);
            if (!info.IsAuthenticated || !(info.IsApiKey || (info.User is { } user && _library.UserCanUse(user))))
            {
                return false;
            }

            var who = (info.Client ?? string.Empty) + " " + ctx.Request.Headers.UserAgent;
            return names.Any(n => who.Contains(n, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const int MaxSyncTracks = 400;
    private const int MaxSyncAlbums = 120;
    private const int MaxSyncArtists = 60;

    /// <summary>
    /// The recent results as library entries for a sync of the whole library: songs, albums or artists as asked,
    /// with the extra fields such apps request (dates, sort name, genres, media source).
    /// </summary>
    private List<JsonNode> SyncNodes(HttpRequest req, bool artistsList)
    {
        var days = Math.Clamp(Plugin.Instance?.Configuration.SyncRecentDays ?? 7, 1, 365);
        var recent = _search.Recent(TimeSpan.FromDays(days), 3000).Where(r => r.Track.Meta is not null || !r.Track.IsAlbumStub).ToList();
        var types = req.Query["includeItemTypes"].SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var serverId = _host.SystemId;
        var nodes = new List<JsonNode>();

        if (artistsList)
        {
            var artists = recent
                .SelectMany(r => (r.Track.IsAlbumStub ? new[] { r.Track.PrimaryAlbumArtist } : r.Track.ArtistNames.Append(r.Track.PrimaryAlbumArtist)).Select(n => (r.Track, Name: n, r.LastSeen)))
                .Where(x => x.Name.Length > 0 && _library.ArtistIdByName(x.Name) is null)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(MaxSyncArtists);
            foreach (var a in artists)
            {
                nodes.Add(SyncFields(Artist(a.Track, a.Name, serverId), a.Name, a.LastSeen, null, 0));
            }

            return nodes;
        }

        if (types.Contains("MusicAlbum", StringComparer.OrdinalIgnoreCase))
        {
            var albums = recent
                .Where(r => r.Track.Meta is not null && _library.GetItem(r.Track.AlbumId) is null)
                .GroupBy(r => r.Track.AlbumId)
                .Select(g => g.First())
                .Take(MaxSyncAlbums);
            foreach (var a in albums)
            {
                nodes.Add(RealArtistIds(SyncFields(Album(a.Track, serverId, _search.TracksOfAlbum(a.Track.AlbumId)), a.Track.Meta!.Album, a.LastSeen, a.Track.Meta.Genre, 0)));
            }
        }

        if (types.Contains("Audio", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var t in recent.Where(r => !r.Track.IsAlbumStub && !_library.IsPromoted(r.Track.TrackId)).Take(MaxSyncTracks))
            {
                nodes.Add(RealArtistIds(SyncFields(Item(t.Track, serverId), t.Track.DisplayTitle, t.LastSeen, t.Track.Meta?.Genre, t.Track.RunTimeTicks)));
            }
        }

        return nodes;
    }

    private static JsonObject SyncFields(JsonObject node, string name, DateTime seen, string? genre, long runTimeTicks)
    {
        node["DateCreated"] = DateTime.SpecifyKind(seen, DateTimeKind.Utc).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        node["SortName"] = name.ToLowerInvariant();
        node["Genres"] = genre is { Length: > 0 } ? new JsonArray(JsonValue.Create(genre)) : new JsonArray();
        node["Tags"] = new JsonArray();
        if (node["Type"]?.GetValue<string>() == "Audio")
        {
            var id = node["Id"]!.GetValue<string>();
            node["MediaSources"] = new JsonArray(new JsonObject
            {
                ["Protocol"] = "File",
                ["Id"] = id,
                ["Type"] = "Default",
                ["Container"] = "m4a",
                ["RunTimeTicks"] = runTimeTicks,
                ["IsRemote"] = false,
                ["SupportsDirectPlay"] = true,
                ["SupportsDirectStream"] = true,
                ["SupportsTranscoding"] = true,
                ["MediaStreams"] = new JsonArray(new JsonObject { ["Type"] = "Audio", ["Codec"] = "aac", ["Index"] = 0, ["Channels"] = 2, ["SampleRate"] = 44100, ["BitRate"] = 256000, ["IsDefault"] = true }),
            });
        }

        return node;
    }

    /// <summary>Points an entry's artists at the library's own artist when it already has one by that name.</summary>
    private JsonObject RealArtistIds(JsonObject node)
    {
        foreach (var key in new[] { "ArtistItems", "AlbumArtists" })
        {
            if (node[key] is JsonArray list)
            {
                foreach (var a in list.OfType<JsonObject>())
                {
                    if (a["Name"]?.GetValue<string>() is { } name && _library.ArtistIdByName(name) is { } real)
                    {
                        a["Id"] = N(real);
                    }
                }
            }
        }

        return node;
    }

    /// <summary>The songs of an album that is not in the library: remembered ones, or (a catalog album) the catalog's song list.</summary>
    private async Task<IReadOnlyList<TrackResult>> TracksOfVirtualAlbumAsync(TrackResult any, Guid albumId, System.Threading.CancellationToken ct) =>
        any.Source == Sources.Catalog && any.GroupId is not null
            ? await _artists.TracksOfCatalogAlbumAsync(any, ct).ConfigureAwait(false)
            : _search.TracksOfAlbum(albumId);

    private static readonly TimeSpan ProfileBudget = TimeSpan.FromSeconds(20);

    /// <summary>The artist's songs that are missing from the library, from their profiles; the sites' search when the profiles gave nothing.</summary>
    private async Task<IReadOnlyList<TrackResult>> ArtistSongsAsync(string artist, Guid artistId, System.Threading.CancellationToken ct)
    {
        var placement = await _artists.ForArtistAsync(artist, artistId, ProfileBudget, ct, withCatalogSongs: true).ConfigureAwait(false);
        var songs = placement?.AllSongs.ToList();
        return songs is { Count: > 0 } ? songs : await _search.SearchArtistAsync(artist, ct).ConfigureAwait(false);
    }

    /// <summary>One entry per album of the artist's profile that the library does not have.</summary>
    private async Task<IReadOnlyList<TrackResult>> ArtistAlbumsAsync(string artist, Guid artistId, System.Threading.CancellationToken ct) =>
        (await _artists.ForArtistAsync(artist, artistId, ProfileBudget, ct).ConfigureAwait(false))?.AlbumRepresentatives.ToList() ?? new List<TrackResult>();

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
            if (req.ContentLength is null or <= 1_000_000)
            {
                using var reader = new StreamReader(req.Body, leaveOpen: true);
                var buf = new char[1_000_000];
                var n = await reader.ReadBlockAsync(buf, 0, buf.Length);
                Collect(new string(buf, 0, n));
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

        // Downloading writes to disk, so it needs a logged-in user. Anyone else is passed on to Jellyfin, which answers 401.
        if (!await IsAllowedAsync(ctx))
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

    private static readonly string[] ArtistIdKeys = { "artistIds", "albumArtistIds", "contributingArtistIds" };

    /// <summary>The artist an "Items" request is filtered by, if any.</summary>
    private static Guid? ArtistIdOf(HttpRequest req)
    {
        foreach (var key in ArtistIdKeys)
        {
            foreach (var value in req.Query[key])
            {
                foreach (var part in (value ?? string.Empty).Split(',', '|'))
                {
                    if (Guid.TryParse(part, out var id))
                    {
                        return id;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Lets Jellyfin answer, then adds the online results to the JSON (the searches were started before, in parallel).</summary>
    private async Task AppendOnlineAsync(HttpContext ctx, Func<byte[], Task<byte[]?>> edit, string label)
    {
        // Prevent compressed responses so the body can be edited.
        ctx.Request.Headers.Remove("Accept-Encoding");
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
                var edited = await edit(bytes).ConfigureAwait(false);
                if (edited is not null)
                {
                    bytes = edited;
                }

                _logger.LogInformation("{Label}: {Outcome}", label, edited is null ? "nothing to add" : "added online results");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Label}: online lookup failed; returning Jellyfin results only", label);
            }
        }

        ctx.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, ctx.RequestAborted);
    }

    private static readonly TimeSpan SearchProfileBudget = TimeSpan.FromSeconds(8);

    /// <summary>Albums for an album search: real albums of the matching songs, plus the albums of the artist the term names.</summary>
    private async Task<IReadOnlyList<TrackResult>> AlbumsForSearchAsync(string term, IReadOnlyList<TrackResult> results, System.Threading.CancellationToken ct)
    {
        var termKey = ArtistProfileService.TitleKey(term);
        var reps = results
            .Where(r => r.Meta is { } m && (m.Year is not null || m.TrackNumber is not null || m.ArtworkUrl is not null)
                && !MetadataMatcher.IsSpecialEditionName(m.Album)
                && termKey.IsSubsetOf(ArtistProfileService.TitleKey(m.Album + " " + r.DisplayAlbumArtist)))
            .GroupBy(r => r.AlbumId)
            .Select(g => g.First())
            .Where(r => _library.GetItem(r.AlbumId) is null)
            .ToList();

        // The term is an artist's name: add the albums found on that artist's own profile.
        var artist = await _artists.ArtistNamedAsync(term, results, ct).ConfigureAwait(false)
            ?? (await _artists.TemporaryArtistAsync(term, SearchProfileBudget, ct).ConfigureAwait(false))?.Name;
        if (artist is not null)
        {
            var placement = await _artists.ForArtistAsync(artist, _library.ArtistIdByName(artist), SearchProfileBudget, ct).ConfigureAwait(false);
            foreach (var rep in placement?.AlbumRepresentatives ?? Enumerable.Empty<TrackResult>())
            {
                if (!reps.Any(x => x.AlbumId == rep.AlbumId || ArtistProfileService.NamesMatch(x.Meta!.Album, rep.Meta!.Album)))
                {
                    reps.Add(rep);
                }
            }
        }

        return reps.Take(24).ToList();
    }

    /// <summary>Artists named like the search term, taken from the results (most songs first).</summary>
    internal static IReadOnlyList<(TrackResult Track, string Name)> ArtistsForSearch(string term, IReadOnlyList<TrackResult> results)
    {
        var termKey = ArtistProfileService.TitleKey(term);
        return results
            .SelectMany(r => r.ArtistNames.Select(n => (Track: r, Name: n)))
            .Where(x =>
            {
                var key = ArtistProfileService.TitleKey(x.Name);
                return key.Count > 0 && termKey.Count > 0 && termKey.IsSubsetOf(key) && key.Count <= termKey.Count + 1;
            })
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.First())
            .ToList();
    }

    private static string ClientName(HttpRequest req)
    {
        var ua = req.Headers.UserAgent.ToString();
        return ua.Length > 60 ? ua[..60] : ua;
    }

    /// <summary>
    /// Our middleware runs before Jellyfin's own auth, so it must check the caller itself before doing any work: signed in, and
    /// (for a user, not an API key) allowed to use the library downloads go to.
    /// </summary>
    private async Task<bool> IsAllowedAsync(HttpContext ctx)
    {
        try
        {
            var info = await _auth.GetAuthorizationInfo(ctx).ConfigureAwait(false);
            if (!info.IsAuthenticated)
            {
                return false;
            }

            return info.IsApiKey || (info.User is { } user && _library.UserCanUse(user));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task WriteJsonAsync(HttpContext ctx, string json, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }

    private static bool WantsArtists(HttpRequest req) =>
        req.Query["includeItemTypes"].SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)).Contains("MusicArtist", StringComparer.OrdinalIgnoreCase);

    private static bool WantsAlbums(HttpRequest req) =>
        req.Query["includeItemTypes"].SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)).Contains("MusicAlbum", StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Adds entries to the list of a response (Items, or SearchHints), skipping ones it already has (by id or name).</summary>
    private static byte[]? AppendNodes(byte[] body, bool hints, IReadOnlyList<(Guid Id, string Name, JsonNode Node)> nodes)
    {
        if (nodes.Count == 0 || JsonNode.Parse(body) is not JsonObject root || root[hints ? "SearchHints" : "Items"] is not JsonArray arr)
        {
            return null;
        }

        var ids = arr.Select(n => n?["Id"]?.GetValue<string>() ?? n?["ItemId"]?.GetValue<string>()).Where(i => i is not null && Guid.TryParse(i, out _)).Select(i => Guid.Parse(i!)).ToHashSet();
        var names = arr.Select(n => n?["Name"]?.GetValue<string>()).Where(n => n is not null).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = nodes.Where(n => !ids.Contains(n.Id) && !names.Contains(n.Name)).ToList();
        if (fresh.Count == 0)
        {
            return null;
        }

        var existing = arr.Count;
        foreach (var n in fresh)
        {
            arr.Add(n.Node);
        }

        root["TotalRecordCount"] = (root["TotalRecordCount"]?.GetValue<int>() ?? existing) + fresh.Count;
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>Adds albums (one entry each, standing for the album) that the response does not list yet.</summary>
    internal static byte[]? AppendAlbums(byte[] body, IReadOnlyList<TrackResult> representatives, bool hints, string term, string serverId, Func<Guid, IReadOnlyList<TrackResult>> tracksOf) =>
        AppendNodes(
            body,
            hints,
            representatives.Where(r => r.Meta is not null)
                .Select(r => (r.AlbumId, r.Meta!.Album, (JsonNode)(hints ? AlbumHint(r, term) : Album(r, serverId, tracksOf(r.AlbumId)))))
                .ToList());

    /// <summary>Adds artists that the response does not list yet.</summary>
    internal static byte[]? AppendArtists(byte[] body, IReadOnlyList<(TrackResult Track, string Name)> artists, bool hints, string term, string serverId) =>
        AppendNodes(
            body,
            hints,
            artists.Select(a => (a.Track.ArtistIdFor(a.Name), a.Name, (JsonNode)(hints ? ArtistHint(a.Track, a.Name, term) : Artist(a.Track, a.Name, serverId)))).ToList());

    private static JsonObject AlbumHint(TrackResult r, string term) => new()
    {
        ["ItemId"] = N(r.AlbumId),
        ["Id"] = N(r.AlbumId),
        ["Name"] = r.Meta!.Album,
        ["MatchedTerm"] = term,
        ["PrimaryImageTag"] = r.ImageTag,
        ["Type"] = "MusicAlbum",
        ["IsFolder"] = true,
        ["MediaType"] = "Unknown",
        ["ProductionYear"] = r.Meta.Year,
        ["AlbumArtist"] = r.PrimaryAlbumArtist,
    };

    private static JsonObject ArtistHint(TrackResult r, string name, string term) => new()
    {
        ["ItemId"] = N(r.ArtistIdFor(name)),
        ["Id"] = N(r.ArtistIdFor(name)),
        ["Name"] = name,
        ["MatchedTerm"] = term,
        ["PrimaryImageTag"] = r.ImageTag,
        ["Type"] = "MusicArtist",
        ["IsFolder"] = true,
        ["MediaType"] = "Unknown",
    };

    internal static JsonObject Album(TrackResult r, string serverId, IReadOnlyList<TrackResult> tracks) => new()
    {
        ["Name"] = r.Meta!.Album,
        ["ServerId"] = serverId,
        ["Id"] = N(r.AlbumId),
        ["Type"] = "MusicAlbum",
        ["IsFolder"] = true,
        ["AlbumArtist"] = r.PrimaryAlbumArtist,
        ["AlbumArtists"] = new JsonArray(new JsonObject { ["Name"] = r.PrimaryAlbumArtist, ["Id"] = N(r.ArtistIdFor(r.PrimaryAlbumArtist)) }),
        ["ArtistItems"] = new JsonArray(new JsonObject { ["Name"] = r.PrimaryAlbumArtist, ["Id"] = N(r.ArtistIdFor(r.PrimaryAlbumArtist)) }),
        ["ProductionYear"] = r.Meta.Year,
        ["ChildCount"] = Math.Max(1, tracks.Count),
        ["RunTimeTicks"] = tracks.Sum(t => t.RunTimeTicks),
        ["ImageTags"] = new JsonObject { ["Primary"] = r.ImageTag },
        ["BackdropImageTags"] = new JsonArray(),
        ["CanDelete"] = false,
        ["UserData"] = new JsonObject { ["PlaybackPositionTicks"] = 0, ["PlayCount"] = 0, ["IsFavorite"] = false, ["Played"] = false },
    };

    /// <summary>The page of an artist that is not in the library yet (its songs come from the sites).</summary>
    internal static JsonObject Artist(TrackResult r, string name, string serverId) => new()
    {
        ["Name"] = name,
        ["ServerId"] = serverId,
        ["Id"] = N(r.ArtistIdFor(name)),
        ["Type"] = "MusicArtist",
        ["IsFolder"] = true,
        ["ImageTags"] = new JsonObject { ["Primary"] = r.ImageTag },
        ["BackdropImageTags"] = new JsonArray(),
        ["CanDelete"] = false,
        ["UserData"] = new JsonObject { ["PlaybackPositionTicks"] = 0, ["PlayCount"] = 0, ["IsFavorite"] = false, ["Played"] = false },
    };

    private static string N(Guid g) => g.ToString("N");

    internal static JsonObject Item(TrackResult r, string serverId)
    {
        var item = new JsonObject
        {
            ["Name"] = r.DisplayTitle,
            ["ServerId"] = serverId,
            ["Id"] = N(r.TrackId),
            ["CanDelete"] = false,
            ["CanDownload"] = true,
            ["RunTimeTicks"] = r.RunTimeTicks,
            ["ProductionYear"] = r.Meta?.Year,
            ["IndexNumber"] = r.Meta?.TrackNumber,
            ["ParentIndexNumber"] = r.Meta?.DiscNumber,
            ["IsFolder"] = false,
            ["Type"] = "Audio",
            ["MediaType"] = "Audio",
            ["Artists"] = new JsonArray(r.ArtistNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["ArtistItems"] = new JsonArray(r.ArtistNames.Select(n => (JsonNode?)new JsonObject { ["Name"] = n, ["Id"] = N(r.ArtistIdFor(n)) }).ToArray()),
            ["AlbumArtist"] = r.PrimaryAlbumArtist,
            ["AlbumArtists"] = new JsonArray(new JsonObject { ["Name"] = r.PrimaryAlbumArtist, ["Id"] = N(r.ArtistIdFor(r.PrimaryAlbumArtist)) }),
            ["ImageTags"] = new JsonObject { ["Primary"] = r.ImageTag },
            ["BackdropImageTags"] = new JsonArray(),
            ["LocationType"] = "FileSystem",
            ["UserData"] = new JsonObject { ["PlaybackPositionTicks"] = 0, ["PlayCount"] = 0, ["IsFavorite"] = false, ["Played"] = false, ["Key"] = N(r.TrackId) },
        };

        // Only claim an album when a real one is known; otherwise it is a loose track, like any untagged file.
        if (r.Meta is { } m)
        {
            item["Album"] = m.Album;
            item["AlbumId"] = N(r.AlbumId);
            item["AlbumPrimaryImageTag"] = r.ImageTag;
        }

        return item;
    }

    private static JsonObject Hint(TrackResult r, string term)
    {
        var hint = new JsonObject
        {
            ["ItemId"] = N(r.TrackId),
            ["Id"] = N(r.TrackId),
            ["Name"] = r.DisplayTitle,
            ["MatchedTerm"] = term,
            ["PrimaryImageTag"] = r.ImageTag,
            ["Type"] = "Audio",
            ["IsFolder"] = false,
            ["RunTimeTicks"] = r.RunTimeTicks,
            ["MediaType"] = "Audio",
            ["IndexNumber"] = r.Meta?.TrackNumber,
            ["ProductionYear"] = r.Meta?.Year,
            ["AlbumArtist"] = r.PrimaryAlbumArtist,
            ["Artists"] = new JsonArray(r.ArtistNames.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        };
        if (r.Meta is { } m)
        {
            hint["Album"] = m.Album;
            hint["AlbumId"] = N(r.AlbumId);
        }

        return hint;
    }
}
