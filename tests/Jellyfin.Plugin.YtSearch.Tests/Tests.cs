using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.YtSearch.Middleware;
using Jellyfin.Plugin.YtSearch.Services;
using Xunit;

namespace Jellyfin.Plugin.YtSearch.Tests;

public class Tests
{
    private const string SearchJson = """
    {"entries":[
      {"id":"dQw4w9WgXcQ","title":"Never Gonna Give You Up","channel":"Rick Astley","duration":213.0},
      {"id":"UC1234567890123456789012","title":"A channel","duration":null},
      {"id":"liveliveliv","title":"Live","channel":"X","duration":null},
      {"id":"abcdefghijk","title":"No channel","uploader":"Up","duration":60}
    ]}
    """;

    [Fact]
    public void ParsesOnlyVideos()
    {
        var r = YtDlpService.ParseSearchJson(SearchJson);
        Assert.Equal(new[] { "dQw4w9WgXcQ", "abcdefghijk" }, r.Select(x => x.VideoId));
        Assert.Equal("Up", r[1].Channel);
        Assert.Equal(213 * 10_000_000L, r[0].RunTimeTicks);
    }

    [Fact]
    public void IdsAreStableAndDistinct()
    {
        var a = YtDlpService.ParseSearchJson(SearchJson)[0];
        var b = YtDlpService.ParseSearchJson(SearchJson)[0];
        Assert.Equal(a.TrackId, b.TrackId);
        Assert.NotEqual(a.TrackId, a.AlbumId);
    }

    [Fact]
    public void AppendsToItems()
    {
        var yt = YtDlpService.ParseSearchJson(SearchJson);
        var body = Encoding.UTF8.GetBytes("""{"Items":[{"Name":"local"}],"TotalRecordCount":1,"StartIndex":0}""");
        var o = (JsonObject)JsonNode.Parse(SearchAppendMiddleware.Append(body, yt, false, "rick", "srv")!)!;
        Assert.Equal(3, o["Items"]!.AsArray().Count);
        Assert.Equal(3, (int)o["TotalRecordCount"]!);
        Assert.Equal("Audio", (string)o["Items"]![1]!["Type"]!);
        Assert.Equal("srv", (string)o["Items"]![1]!["ServerId"]!);
    }

    [Fact]
    public void AppendsToHints()
    {
        var yt = YtDlpService.ParseSearchJson(SearchJson);
        var body = Encoding.UTF8.GetBytes("""{"SearchHints":[],"TotalRecordCount":0}""");
        var o = (JsonObject)JsonNode.Parse(SearchAppendMiddleware.Append(body, yt, true, "rick", "srv")!)!;
        Assert.Equal(2, o["SearchHints"]!.AsArray().Count);
        Assert.Equal(2, (int)o["TotalRecordCount"]!);
    }
}

public class CookieTests
{
    private const string Sample =
        "# Netscape HTTP Cookie File\r\n" +
        ".youtube.com\tTRUE\t/\tTRUE\t4102444800\t__Secure-3PSID\tabc\r\n" +
        "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1\tSID\txyz\r\n" +
        ".example.com\tTRUE\t/\tFALSE\t0\tfoo\tbar\r\n" +
        "garbage line\r\n";

    [Fact]
    public void ParsesNetscapeIncludingHttpOnly()
    {
        var c = Jellyfin.Plugin.YtSearch.Services.CookieService.Parse(Jellyfin.Plugin.YtSearch.Services.CookieService.Normalize(Sample));
        Assert.Equal(3, c.Count);
        Assert.Contains(c, x => x.Name == "SID" && x.Domain == "youtube.com" && x.Expiry == 1);
    }

    [Theory]
    [InlineData("ERROR: The provided YouTube account cookies are no longer valid. They have likely been rotated", "failing")]
    [InlineData("ERROR: Sign in to confirm you're not a bot", "failing")]
    [InlineData("ERROR: Unable to download webpage: timed out", "error")]
    public void ClassifiesFailures(string stderr, string state) =>
        Assert.Equal(state, Jellyfin.Plugin.YtSearch.Services.YtDlpService.ClassifyFailure(stderr).State);
}
