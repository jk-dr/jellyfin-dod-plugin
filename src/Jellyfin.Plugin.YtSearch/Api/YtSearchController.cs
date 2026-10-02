using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YtSearch.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.YtSearch.Api;

public class SearchRow
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("album")]
    public string? Album { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("seconds")]
    public int Seconds { get; set; }
}

public class AddResult
{
    [System.Text.Json.Serialization.JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class CookieUpload
{
    public string Cookies { get; set; } = string.Empty;
}

/// <summary>Admin-only API used by the plugin settings page.</summary>
[ApiController]
[Route("YtSearch")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class YtSearchController : ControllerBase
{
    private readonly CookieService _cookies;
    private readonly YtDlpService _ytdlp;
    private readonly SearchService _search;
    private readonly DownloadService _downloads;
    private readonly FailureLog _failures;

    public YtSearchController(CookieService cookies, YtDlpService ytdlp, SearchService search, DownloadService downloads, FailureLog failures)
    {
        _cookies = cookies;
        _ytdlp = ytdlp;
        _search = search;
        _downloads = downloads;
        _failures = failures;
    }

    /// <summary>The last failed downloads with the reason (a client only shows a generic playback error).</summary>
    [HttpGet("Failures")]
    public ActionResult<IReadOnlyList<FailureEntry>> GetFailures() => Ok(_failures.Recent());

    /// <summary>Search for the settings page's "Find and add" box. Same results (and ids) a client would get.</summary>
    [HttpGet("Search")]
    public async Task<ActionResult<IReadOnlyList<SearchRow>>> Search([FromQuery] string q, CancellationToken ct)
    {
        var results = await _search.SearchAsync(q ?? string.Empty, ct);
        return Ok(results.Select(r => new SearchRow
        {
            Id = r.TrackId.ToString("N"),
            Title = r.DisplayTitle,
            Artist = r.DisplayArtist,
            Album = r.Meta?.Album,
            Source = r.Source,
            Seconds = (int)r.DurationSeconds,
        }).ToList());
    }

    /// <summary>Downloads a search result into the library now, so apps that only show their synced library (Manet) get it on the next sync.</summary>
    [HttpPost("Add")]
    public async Task<ActionResult<AddResult>> Add([FromQuery] string id, CancellationToken ct)
    {
        if (!System.Guid.TryParse(id, out var guid) || !_search.TryResolve(guid, out var track) || track.TrackId != guid)
        {
            return NotFound(new ProblemDetails { Title = "Unknown track", Detail = "Search again, then add it.", Status = StatusCodes.Status404NotFound });
        }

        try
        {
            await _downloads.EnsureAsync(track, ct);
            return Ok(new AddResult { Ok = true, Message = "Added to the library." });
        }
        catch (DownloadException ex)
        {
            return Ok(new AddResult { Ok = false, Message = ex.Message });
        }
    }

    [HttpGet("Cookies")]
    public ActionResult<CookieStatus> GetCookies() => _cookies.GetStatus();

    [HttpPut("Cookies")]
    [RequestSizeLimit(2_000_000)]
    public ActionResult<CookieStatus> PutCookies([FromBody] CookieUpload body)
    {
        try
        {
            _cookies.Save(body.Cookies ?? string.Empty);
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid cookies", Detail = ex.Message, Status = StatusCodes.Status400BadRequest });
        }

        return _cookies.GetStatus();
    }

    [HttpDelete("Cookies")]
    public ActionResult<CookieStatus> DeleteCookies()
    {
        _cookies.Delete();
        return _cookies.GetStatus();
    }

    /// <summary>Live check against YouTube (takes a few seconds).</summary>
    [HttpPost("Cookies/Check")]
    public async Task<ActionResult<CookieStatus>> CheckCookies(CancellationToken ct)
    {
        await _ytdlp.CheckCookiesAsync(ct);
        return _cookies.GetStatus();
    }
}
