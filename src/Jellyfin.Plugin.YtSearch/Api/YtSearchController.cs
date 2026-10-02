using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YtSearch.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.YtSearch.Api;

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

    public YtSearchController(CookieService cookies, YtDlpService ytdlp)
    {
        _cookies = cookies;
        _ytdlp = ytdlp;
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
