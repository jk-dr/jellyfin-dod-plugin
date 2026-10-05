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
        Assert.Equal(new[] { "dQw4w9WgXcQ", "abcdefghijk" }, r.Select(x => x.SourceId));
        Assert.Equal("Up", r[1].Artist);
        Assert.Equal(213 * 10_000_000L, r[0].RunTimeTicks);
    }

    private const string ScJson = """
    {"entries":[
      {"id":"253508261","title":"Never Gonna Give You Up","uploader":"Rick Astley","duration":213.619,"webpage_url":"https://soundcloud.com/rick-astley-official/never-gonna-give-you-up","thumbnails":[{"url":"https://i1.sndcdn.com/a-original.jpg"}]},
      {"id":"2","title":"No art","uploader":"U","duration":10,"webpage_url":"https://soundcloud.com/u/no-art"},
      {"id":"3","title":"No page","uploader":"U","duration":10}
    ]}
    """;

    [Fact]
    public void ParsesSoundCloud()
    {
        var r = YtDlpService.ParseSearchJson(ScJson, Sources.SoundCloud);
        Assert.Equal(2, r.Count);
        Assert.Equal("Rick Astley", r[0].Artist);
        Assert.Equal("https://i1.sndcdn.com/a-original.jpg", r[0].ThumbnailUrl);
        Assert.Equal("", r[1].ThumbnailUrl);
        Assert.Equal("sc253508261", r[0].ImageTag);
    }

    [Fact]
    public void SourcesGetDifferentIdsForSameSourceId()
    {
        var a = new TrackResult(Sources.YouTube, "x", "t", "a", 1, "", "");
        var b = new TrackResult(Sources.SoundCloud, "x", "t", "a", 1, "", "");
        Assert.NotEqual(a.TrackId, b.TrackId);
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

public class CleanupPolicyTests
{
    private static readonly System.DateTime Now = new(2026, 10, 10, 0, 0, 0, System.DateTimeKind.Utc);

    private static bool Delete(int plays = 0, bool fav = false, bool playlist = false, int ageDays = 8) =>
        Jellyfin.Plugin.YtSearch.Services.CleanupPolicy.ShouldDelete(plays, fav, playlist, Now.AddDays(-ageDays), Now, 3, 7);

    [Fact] public void DeletesUnusedOldTrack() => Assert.True(Delete());
    [Fact] public void DeletesAtExactlyThreePlays() => Assert.True(Delete(plays: 3));
    [Fact] public void KeepsAfterFourPlays() => Assert.False(Delete(plays: 4));
    [Fact] public void KeepsFavorite() => Assert.False(Delete(fav: true));
    [Fact] public void KeepsPlaylistTrack() => Assert.False(Delete(playlist: true));
    [Fact] public void KeepsTrackYoungerThanAWeek() => Assert.False(Delete(ageDays: 6));
    [Fact] public void DeletesAtExactlyAWeek() => Assert.True(Delete(ageDays: 7));
}

public class PlayabilityTests
{
    [Theory]
    [InlineData("ERROR: [soundcloud] 253508261: This video is DRM protected", true)]
    [InlineData("ERROR: [soundcloud] 1: Requested format is not available. Use --list-formats", true)]
    [InlineData("ERROR: Unable to download JSON metadata: <urlopen error timed out>", false)]
    [InlineData("ERROR: HTTP Error 429: Too Many Requests", false)]
    public void ClassifiesPermanentFailures(string stderr, bool permanent) =>
        Assert.Equal(permanent, Jellyfin.Plugin.YtSearch.Services.YtDlpService.IsPermanentFailure(stderr));
}

public class SecurityTests
{
    private static readonly System.Func<string, string?, bool> Valid = Jellyfin.Plugin.YtSearch.Services.InputGuard.IsValidSourceId;

    [Theory]
    [InlineData("youtube", "dQw4w9WgXcQ", true)]
    [InlineData("youtube", "abc-_123456", true)]
    [InlineData("youtube", "../../etc/pw", false)]
    [InlineData("youtube", "dQw4w9WgXc/", false)]
    [InlineData("youtube", "short", false)]
    [InlineData("youtube", "-abcdefghij", true)] // real ids may start with "-"; ids only appear inside an https URL and a yt-<id>.m4a name
    [InlineData("soundcloud", "253508261", true)]
    [InlineData("soundcloud", "12/../34", false)]
    [InlineData("soundcloud", "abc", false)]
    [InlineData("soundcloud", "", false)]
    public void ValidatesIds(string source, string id, bool ok) => Assert.Equal(ok, Valid(source, id));

    [Theory]
    [InlineData("https://i.ytimg.com/vi/x/hqdefault.jpg", true)]
    [InlineData("https://i1.sndcdn.com/artworks-a-original.jpg", true)]
    [InlineData("https://yt3.ggpht.com/a", true)]
    [InlineData("http://i.ytimg.com/vi/x/a.jpg", false)]
    [InlineData("https://127.0.0.1/a.jpg", false)]
    [InlineData("https://localhost/a.jpg", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://evilytimg.com/a.jpg", false)]
    [InlineData("https://i.ytimg.com.evil.com/a.jpg", false)]
    [InlineData("https://user:pw@i.ytimg.com/a.jpg", false)]
    [InlineData("https://i.ytimg.com:8443/a.jpg", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///etc/passwd", false)]
    public void ThumbnailUrlsAreAllowlisted(string url, bool ok) =>
        Assert.Equal(ok, Jellyfin.Plugin.YtSearch.Services.InputGuard.SafeThumbnailUrl(url) is not null);

    [Theory]
    [InlineData("https://soundcloud.com/a/b", true)]
    [InlineData("https://api.soundcloud.com/tracks/soundcloud%3Atracks%3A1", true)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("https://evil.com/x", false)]
    [InlineData("-o /etc/cron.d/x", false)]
    [InlineData("http://soundcloud.com/a", false)]
    public void PageUrlsAreAllowlisted(string url, bool ok) =>
        Assert.Equal(ok, Jellyfin.Plugin.YtSearch.Services.InputGuard.SafePageUrl(url) is not null);

    [Fact]
    public void QueryIsCleaned()
    {
        Assert.Equal("a b", Jellyfin.Plugin.YtSearch.Services.InputGuard.CleanQuery("a\r\n\u0000 b\t".Replace("\\r", "\r")).Replace("  ", " ").Replace("\r", string.Empty));
        Assert.Equal(200, Jellyfin.Plugin.YtSearch.Services.InputGuard.CleanQuery(new string('x', 5000)).Length);
        Assert.Equal(string.Empty, Jellyfin.Plugin.YtSearch.Services.InputGuard.CleanQuery("   \n "));
    }

    [Fact]
    public void ParserDropsHostileEntries()
    {
        const string json = """
        {"entries":[
          {"id":"../../etc/pw","title":"x","duration":10},
          {"id":"dQw4w9WgXcQ","title":"<img src=x onerror=alert(1)>\u0007ok","channel":"c","duration":10}
        ]}
        """;
        var r = Jellyfin.Plugin.YtSearch.Services.YtDlpService.ParseSearchJson(json);
        Assert.Single(r);
        Assert.DoesNotContain('\u0007', r[0].Title);

        const string sc = """
        {"entries":[
          {"id":"1","title":"t","duration":10,"webpage_url":"https://evil.com/x","thumbnails":[{"url":"https://127.0.0.1/a.jpg"}]},
          {"id":"2","title":"t","duration":10,"webpage_url":"https://soundcloud.com/a/b","thumbnails":[{"url":"https://127.0.0.1/a.jpg"}]}
        ]}
        """;
        var s = Jellyfin.Plugin.YtSearch.Services.YtDlpService.ParseSearchJson(sc, Jellyfin.Plugin.YtSearch.Services.Sources.SoundCloud);
        Assert.Single(s);
        Assert.Equal(string.Empty, s[0].ThumbnailUrl);
    }

    [Theory]
    [InlineData("/lib/yt-dQw4w9WgXcQ.m4a", true)]
    [InlineData("/lib/sc-253508261.m4a", true)]
    [InlineData("/lib/My Favourite Song.m4a", false)]
    [InlineData("/lib/Daft Punk/Random Access Memories/yt-dQw4w9WgXcQ.m4a", true)]
    [InlineData("/lib/Artist/yt-dQw4w9WgXcQ.m4a", true)]
    [InlineData("/lib/a/b/c/yt-dQw4w9WgXcQ.m4a", false)]
    [InlineData("/lib/../etc/yt-dQw4w9WgXcQ.m4a", false)]
    [InlineData("/library2/yt-dQw4w9WgXcQ.m4a", false)]
    [InlineData("/other/yt-dQw4w9WgXcQ.m4a", false)]
    [InlineData("/lib/yt-dQw4w9WgXcQ.mp3", false)]
    [InlineData(null, false)]
    public void CleanupOnlyTouchesOurFiles(string? path, bool ours) =>
        Assert.Equal(ours, Jellyfin.Plugin.YtSearch.Services.LibraryService.IsOurFile("/lib", path));
}

public class RankingTests
{
    private static Jellyfin.Plugin.YtSearch.Services.TrackResult T(string src, string id, string title, string artist, double dur = 200) =>
        new(src, id, title, artist, dur, "", "");

    [Fact]
    public void ExactSongBeatsRemixesAndMixesAcrossSources()
    {
        var yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
        var sc = Jellyfin.Plugin.YtSearch.Services.Sources.SoundCloud;
        var input = new[]
        {
            T(yt, "11111111111", "Rick Astley Never Gonna Give You Up Slowed + Reverb", "Chill Edits"),
            T(yt, "22222222222", "Never Gonna Give You Up (Karaoke Version)", "Sing King"),
            T(yt, "33333333333", "Rick Astley - Never Gonna Give You Up (Official Video)", "Rick Astley", 213),
            T(sc, "1", "Never Gonna Give You Up", "Rick Astley", 213),
            T(sc, "2", "Never Gonna Give You Up - Rick Astley (Dubstep Remix)", "DJ X"),
            T(yt, "44444444444", "Best of 80s - 2 hour mix", "Retro", 7300),
        };
        var ranked = Jellyfin.Plugin.YtSearch.Services.RelevanceRanker.Rank("rick astley never gonna give you up", input);
        var ids = ranked.Select(r => r.SourceId).ToList();
        // the two clean versions (one per source) lead, interleaved by accuracy not by source
        Assert.Equal(new[] { "33333333333", "1" }, ids.Take(2).OrderBy(x => x == "1" ? 1 : 0).ToArray());
        Assert.True(ids.IndexOf("4" + "4444444444") > ids.IndexOf("1"));
        Assert.True(ids.IndexOf("33333333333") < ids.IndexOf("22222222222"));
        Assert.True(ids.IndexOf("1") < ids.IndexOf("2"));
    }

    [Fact]
    public void SourcesAreInterleavedNotGrouped()
    {
        var yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
        var sc = Jellyfin.Plugin.YtSearch.Services.Sources.SoundCloud;
        var input = new[]
        {
            T(yt, "aaaaaaaaaaa", "Daft Punk - Get Lucky live at festival remix", "Fan"),
            T(yt, "bbbbbbbbbbb", "Daft Punk - Get Lucky", "Daft Punk"),
            T(sc, "10", "Get Lucky", "Daft Punk"),
            T(sc, "11", "Get Lucky cover", "Someone"),
        };
        var ids = Jellyfin.Plugin.YtSearch.Services.RelevanceRanker.Rank("daft punk get lucky", input).Select(r => r.SourceId).ToList();
        Assert.Equal(new[] { "bbbbbbbbbbb", "10" }, ids.Take(2).OrderBy(x => x == "10" ? 1 : 0).ToArray());
    }

    [Fact]
    public void AccentsAndPunctuationDontMatter() =>
        Assert.Equal(new[] { "beyonce", "halo" }, Jellyfin.Plugin.YtSearch.Services.RelevanceRanker.Tokens("Beyoncé - Halo!"));

    [Fact]
    public void KeepsOrderWhenNothingToCompare() =>
        Assert.Single(Jellyfin.Plugin.YtSearch.Services.RelevanceRanker.Rank("x", new[] { T("youtube", "aaaaaaaaaaa", "t", "a") }));
}

public class OnlineParserTests
{
    private const string YouTubeJson = """
    {"contents":{"twoColumnSearchResultsRenderer":{"primaryContents":{"sectionListRenderer":{"contents":[
      {"itemSectionRenderer":{"contents":[
        {"videoRenderer":{"videoId":"dQw4w9WgXcQ","title":{"runs":[{"text":"Rick Astley - Never Gonna Give You Up"}]},"ownerText":{"runs":[{"text":"Rick Astley"}]},"lengthText":{"simpleText":"3:34"},"thumbnail":{"thumbnails":[{"url":"https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg"},{"url":"https://i.ytimg.com/vi/dQw4w9WgXcQ/hq720.jpg?sqp=abc"}]}}},
        {"videoRenderer":{"videoId":"liveliveliv","title":{"runs":[{"text":"24/7 radio"}]},"ownerText":{"runs":[{"text":"Radio"}]}}},
        {"shelfRenderer":{"title":"People also watched"}},
        {"videoRenderer":{"videoId":"../../../etc","title":{"runs":[{"text":"evil"}]},"lengthText":{"simpleText":"1:00"}}},
        {"videoRenderer":{"videoId":"abcdefghijk","title":{"runs":[{"text":"Long "},{"text":"Mix"}]},"ownerText":{"runs":[{"text":"DJ"}]},"lengthText":{"simpleText":"1:02:03"},"thumbnail":{"thumbnails":[{"url":"https://evil.example/x.jpg"}]}}}
      ]}}
    ]}}}}}
    """;

    [Fact]
    public void ParsesYouTubeSearch()
    {
        var r = Jellyfin.Plugin.YtSearch.Services.OnlineSearchClient.ParseYouTube(YouTubeJson, 10)!;
        Assert.Equal(new[] { "dQw4w9WgXcQ", "abcdefghijk" }, r.Select(x => x.SourceId));
        Assert.Equal(214, r[0].DurationSeconds);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/hq720.jpg?sqp=abc", r[0].ThumbnailUrl);
        Assert.Equal("Long Mix", r[1].Title);
        Assert.Equal(3723, r[1].DurationSeconds);
        Assert.StartsWith("https://i.ytimg.com/", r[1].ThumbnailUrl); // hostile thumbnail host replaced
    }

    [Fact]
    public void YouTubeUnexpectedShapeReturnsNullSoCallerFallsBack() =>
        Assert.Null(Jellyfin.Plugin.YtSearch.Services.OnlineSearchClient.ParseYouTube("{\"contents\":{}}", 10));

    [Fact]
    public void YouTubeRespectsMax() =>
        Assert.Single(Jellyfin.Plugin.YtSearch.Services.OnlineSearchClient.ParseYouTube(YouTubeJson, 1)!);

    private const string ScJson = """
    {"collection":[
      {"kind":"track","id":1,"title":"DRM","duration":200000,"permalink_url":"https://soundcloud.com/a/drm","user":{"username":"A"},"artwork_url":"https://i1.sndcdn.com/artworks-x-large.jpg",
       "media":{"transcodings":[{"snipped":false,"format":{"protocol":"cbc-encrypted-hls","mime_type":"audio/mp4; codecs=\"mp4a.40.2\""}},{"snipped":false,"format":{"protocol":"ctr-encrypted-hls","mime_type":"audio/mp4; codecs=\"mp4a.40.2\""}}]}},
      {"kind":"track","id":2,"title":"Preview only","duration":200000,"permalink_url":"https://soundcloud.com/a/prev","user":{"username":"A"},
       "media":{"transcodings":[{"snipped":true,"format":{"protocol":"hls","mime_type":"audio/mpeg"}}]}},
      {"kind":"track","id":3,"title":"Good","duration":213619,"permalink_url":"https://soundcloud.com/a/good","user":{"username":"Rick"},"artwork_url":"https://i1.sndcdn.com/artworks-x-large.jpg",
       "media":{"transcodings":[{"snipped":false,"format":{"protocol":"hls","mime_type":"audio/mp4; codecs=\"mp4a.40.2\""}},{"snipped":false,"format":{"protocol":"progressive","mime_type":"audio/mpeg"}}]}},
      {"kind":"playlist","id":4,"title":"A playlist"},
      {"kind":"track","id":5,"title":"Evil page","duration":1000,"permalink_url":"https://evil.example/x","media":{"transcodings":[]}}
    ]}
    """;

    [Fact]
    public void SoundCloudHidesDrmAndPreviewOnly()
    {
        var r = Jellyfin.Plugin.YtSearch.Services.OnlineSearchClient.ParseSoundCloud(ScJson, 10, true)!;
        Assert.Equal(new[] { "3" }, r.Select(x => x.SourceId));
        Assert.Equal("Rick", r[0].Artist);
        Assert.Equal(213.619, r[0].DurationSeconds, 3);
        Assert.Equal("https://i1.sndcdn.com/artworks-x-t500x500.jpg", r[0].ThumbnailUrl);
    }

    [Fact]
    public void SoundCloudKeepsEverythingWhenFilterOff()
    {
        var r = Jellyfin.Plugin.YtSearch.Services.OnlineSearchClient.ParseSoundCloud(ScJson, 10, false)!;
        Assert.Equal(new[] { "1", "2", "3" }, r.Select(x => x.SourceId)); // playlist and evil-host entries dropped regardless
    }

    [Theory]
    [InlineData("3:34", 214)]
    [InlineData("1:02:03", 3723)]
    [InlineData("0:45", 45)]
    [InlineData("LIVE", 0)]
    [InlineData(null, 0)]
    public void ParsesDurations(string? text, double seconds) =>
        Assert.Equal(seconds, Jellyfin.Plugin.YtSearch.Services.OnlineSearchClient.ParseDuration(text));
}


public class CatalogMatchTests
{
    private static Jellyfin.Plugin.YtSearch.Services.TrackResult T(string src, string id, string title, string artist, double dur) => new(src, id, title, artist, dur, "", "");

    private static Jellyfin.Plugin.YtSearch.Services.CatalogSong S(string title, string artist, string album, double dur, int? year = 2013, string? albumArtist = null) =>
        new(title, artist, album, albumArtist ?? artist, year, 8, 1, "Pop", dur, null);

    private static readonly Jellyfin.Plugin.YtSearch.Services.CatalogSong[] Catalog =
    {
        S("Get Lucky (feat. Pharrell Williams & Nile Rodgers)", "Daft Punk", "Get Lucky (feat. Pharrell Williams & Nile Rodgers) - Single", 369),
        S("Get Lucky (feat. Pharrell Williams & Nile Rodgers)", "Daft Punk", "Random Access Memories", 369),
        S("Get Lucky (feat. Pharrell Williams & Nile Rodgers)", "Daft Punk", "Now That's What I Call Music", 369, 2014, "Various Artists"),
        S("Lose Yourself to Dance", "Daft Punk", "Random Access Memories", 354),
    };

    [Fact]
    public void MatchesToTheRealAlbumNotSingleCompilationOrDeluxe()
    {
        var yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
        var catalog = Catalog.Concat(new[]
        {
            S("Get Lucky (feat. Pharrell Williams & Nile Rodgers)", "Daft Punk", "2014 GRAMMY Nominees", 248, 2014, "Various Artists"),
            S("Get Lucky (feat. Pharrell Williams & Nile Rodgers)", "Daft Punk", "Random Access Memories (10th Anniversary Edition)", 369, 2023),
        }).ToArray();
        var ranked = new[] { T(yt, "aaaaaaaaaaa", "Daft Punk - Get Lucky (Official Audio) ft. Pharrell Williams, Nile Rodgers", "Daft Punk - Topic", 248) };
        var m = Jellyfin.Plugin.YtSearch.Services.MetadataMatcher.Match(ranked, catalog);
        Assert.Equal("Random Access Memories", m["youtube:aaaaaaaaaaa"].Album);
        Assert.Equal("Daft Punk", m["youtube:aaaaaaaaaaa"].AlbumArtist);
    }

    [Fact]
    public void SongGoesToTheArtistsOwnUploadNotAFanUpload()
    {
        var yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
        var ranked = new[]
        {
            T(yt, "fanfanfanfa", "Get Lucky (Feat. Pharrell Williams) Daft Punk", "EXPO STORY", 369),
            T(yt, "officialaaa", "Daft Punk - Get Lucky (Official Audio)", "Daft Punk", 369),
        };
        var m = Jellyfin.Plugin.YtSearch.Services.MetadataMatcher.Match(ranked, Catalog);
        Assert.Single(m);
        Assert.True(m.ContainsKey("youtube:officialaaa"));
    }

    [Fact]
    public void AbsurdLengthsDoNotMatch()
    {
        var yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
        var ranked = new[] { T(yt, "aaaaaaaaaaa", "Daft Punk - Get Lucky (10 hour loop)", "Daft Punk", 36000) };
        Assert.Empty(Jellyfin.Plugin.YtSearch.Services.MetadataMatcher.Match(ranked, Catalog));
    }

    [Fact]
    public void RemixesCoversAndLiveVersionsNeverMatch()
    {
        var yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
        var sc = Jellyfin.Plugin.YtSearch.Services.Sources.SoundCloud;
        var ranked = new[]
        {
            T(sc, "1", "Daft Punk - Get Lucky (Twin Diplomacy Remix)", "Twin Diplomacy", 369),
            T(yt, "bbbbbbbbbbb", "Get Lucky - Daft Punk (Live at Coachella)", "Fan", 369),
            T(yt, "ccccccccccc", "Get Lucky cover by someone", "Someone", 369),
            T(yt, "ddddddddddd", "Daft Punk Get Lucky Slowed + Reverb", "Edits", 369),
        };
        Assert.Empty(Jellyfin.Plugin.YtSearch.Services.MetadataMatcher.Match(ranked, Catalog));
    }

    [Fact]
    public void EachCatalogSongIsUsedOnceByTheBestRankedResult()
    {
        var yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
        var sc = Jellyfin.Plugin.YtSearch.Services.Sources.SoundCloud;
        var ranked = new[]
        {
            T(sc, "1", "Get Lucky", "Daft Punk", 369),
            T(yt, "aaaaaaaaaaa", "Daft Punk - Get Lucky (Lyrics)", "Lyrics Channel", 369),
        };
        var m = Jellyfin.Plugin.YtSearch.Services.MetadataMatcher.Match(ranked, Catalog);
        Assert.Single(m);
        Assert.True(m.ContainsKey("soundcloud:1")); // the one from the artist's own account
    }

    [Fact]
    public void WrongArtistDoesNotMatch()
    {
        var yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
        var ranked = new[] { T(yt, "aaaaaaaaaaa", "Get Lucky", "Some Cover Band", 369) };
        Assert.Empty(Jellyfin.Plugin.YtSearch.Services.MetadataMatcher.Match(ranked, Catalog));
    }

    [Fact]
    public void ParsesItunesResults()
    {
        const string json = """
        {"resultCount":2,"results":[
          {"wrapperType":"track","kind":"song","trackName":"Get Lucky","artistName":"Daft Punk","collectionName":"Random Access Memories","collectionArtistName":"Daft Punk","trackNumber":8,"discNumber":1,"releaseDate":"2013-05-17T07:00:00Z","primaryGenreName":"Electronic","trackTimeMillis":369626,"artworkUrl100":"https://is1-ssl.mzstatic.com/image/thumb/a/100x100bb.jpg"},
          {"wrapperType":"track","kind":"music-video","trackName":"Get Lucky","artistName":"Daft Punk","collectionName":"x"}
        ]}
        """;
        var songs = Jellyfin.Plugin.YtSearch.Services.CatalogClient.ParseSongs(json);
        Assert.Single(songs);
        Assert.Equal(2013, songs[0].Year);
        Assert.Equal(8, songs[0].TrackNumber);
        Assert.Equal(369.626, songs[0].DurationSeconds, 3);
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/a/600x600bb.jpg", songs[0].ArtworkUrl);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData("a/../../b")]
    [InlineData("..\\..\\windows")]
    public void FolderNamesCannotTraverse(string input)
    {
        var name = Jellyfin.Plugin.YtSearch.Services.InputGuard.SafeFolderName(input);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.NotEqual("..", name);
        Assert.NotEqual(".", name);
        Assert.DoesNotContain("..", name);
    }

    [Theory]
    [InlineData("Daft Punk", "Daft Punk")]
    [InlineData("AC/DC", "AC DC")]
    [InlineData("..", "Unknown")]
    [InlineData("   ", "Unknown")]
    [InlineData("What?: A \"Song\" <3", "What A Song 3")]
    public void FolderNamesAreSafe(string input, string expected) =>
        Assert.Equal(expected, Jellyfin.Plugin.YtSearch.Services.InputGuard.SafeFolderName(input).Replace(" <3", " 3").Replace("  ", " "));
}

public class AlbumPolicyTests
{
    private static Jellyfin.Plugin.YtSearch.Services.TrackResult T() => new("youtube", "aaaaaaaaaaa", "Some Song (Official)", "Some Channel", 200, "", "");
    private static readonly Jellyfin.Plugin.YtSearch.Services.TrackMeta Catalog = new("Some Song", "Real Artist", "Real Album", "Real Artist", 2020, 3, 1, "Pop", "https://is1-ssl.mzstatic.com/a.jpg");

    [Fact]
    public void AnArtistPrefixIsDroppedFromTheTitle()
    {
        var r = new Jellyfin.Plugin.YtSearch.Services.TrackResult("soundcloud", "1", "Deede Collective - Hamsafar", "Deede Collective", 106, "", "");
        Assert.Equal("Hamsafar", Jellyfin.Plugin.YtSearch.Services.AlbumPolicy.For(r, null).Title);
        Assert.Equal("Other - Song", Jellyfin.Plugin.YtSearch.Services.AlbumPolicy.StripArtistPrefix("Other - Song", new[] { "Deede Collective" }));
        Assert.Equal("Queen - ", Jellyfin.Plugin.YtSearch.Services.AlbumPolicy.StripArtistPrefix("Queen - ", new[] { "Queen" }));
    }

    [Fact]
    public void CatalogMatchIsKept()
    {
        Assert.Same(Catalog, Jellyfin.Plugin.YtSearch.Services.AlbumPolicy.For(T(), Catalog));
    }

    [Fact]
    public void SongsWithoutAnAlbumGetAnAlbumNamedAfterTheSong()
    {
        var m = Jellyfin.Plugin.YtSearch.Services.AlbumPolicy.For(T(), null);
        Assert.Equal("Some Song (Official)", m.Album);
        Assert.Equal("Some Channel", m.Artist);
        Assert.Equal("Some Channel", m.AlbumArtist);
        Assert.Equal("Some Song (Official)", m.Title);
    }
}

public class ArtistTests
{
    private static System.Collections.Generic.IReadOnlyList<string> Split(string? s) => Jellyfin.Plugin.YtSearch.Services.ArtistSplitter.Split(s);

    [Theory]
    [InlineData("Daft Punk, Pharrell Williams & Nile Rodgers", new[] { "Daft Punk", "Pharrell Williams", "Nile Rodgers" })]
    [InlineData("Simon & Garfunkel", new[] { "Simon & Garfunkel" })]
    [InlineData("Earth, Wind & Fire", new[] { "Earth, Wind & Fire" })]
    [InlineData("Tyler, The Creator", new[] { "Tyler, The Creator" })]
    [InlineData("Billie Eilish feat. Khalid", new[] { "Billie Eilish", "Khalid" })]
    [InlineData("Calvin Harris ft. Rihanna", new[] { "Calvin Harris", "Rihanna" })]
    [InlineData("Metro Boomin x Future", new[] { "Metro Boomin", "Future" })]
    [InlineData("Eminem vs. Rihanna", new[] { "Eminem", "Rihanna" })]
    [InlineData("Daft Punk", new[] { "Daft Punk" })]
    [InlineData("Hall & Oates, Simon & Garfunkel", new[] { "Hall & Oates", "Simon & Garfunkel" })]
    public void SplitsCreditsButKeepsBandNamesWhole(string credit, string[] expected) => Assert.Equal(expected, Split(credit));

    [Fact]
    public void NeverReturnsAnEmptyList()
    {
        Assert.Equal(new[] { string.Empty }, Split(null));
        Assert.Equal(new[] { string.Empty }, Split("   "));
        Assert.Single(Split("&"));
    }

    [Fact]
    public void RemovesDuplicatesIgnoringCase() => Assert.Equal(new[] { "Drake" }, Split("Drake, DRAKE"));

    private static Jellyfin.Plugin.YtSearch.Services.TrackResult T(string artist) =>
        new("youtube", "aaaaaaaaaaa", "Song", artist, 200, "", "");

    [Theory]
    [InlineData("Daft Punk - Topic", "Daft Punk")]
    [InlineData("Daft Punk – Topic", "Daft Punk")]
    [InlineData("Daft Punk", "Daft Punk")]
    [InlineData("Topic", "Topic")]
    [InlineData("- Topic", "- Topic")]
    [InlineData("", "")]
    public void StripsTheAutomaticTopicSuffix(string raw, string expected) =>
        Assert.Equal(expected, Jellyfin.Plugin.YtSearch.Services.TrackResult.StripTopic(raw));

    [Fact]
    public void ArtistsComeFromTheCleanChannelNameAndAreSplit()
    {
        var t = T("Daft Punk - Topic");
        Assert.Equal("Daft Punk", t.DisplayArtist);
        var multi = T("Daft Punk, Pharrell Williams");
        Assert.Equal(new[] { "Daft Punk", "Pharrell Williams" }, multi.ArtistNames);
        Assert.Equal("Daft Punk", multi.PrimaryAlbumArtist);
        Assert.Equal(multi.ArtistIdFor("Daft Punk"), multi.ArtistId);
        Assert.NotEqual(multi.ArtistIdFor("Daft Punk"), multi.ArtistIdFor("Pharrell Williams"));
    }

    [Fact]
    public void FindsWhichArtistAnIdBelongsTo()
    {
        var t = T("Daft Punk, Pharrell Williams");
        Assert.Equal("Pharrell Williams", t.ArtistNameFor(t.ArtistIdFor("Pharrell Williams")));
        Assert.Null(t.ArtistNameFor(System.Guid.NewGuid()));
    }

    [Fact]
    public void ArtistIdsAreSharedAcrossTracksOfTheSameArtist()
    {
        var a = new Jellyfin.Plugin.YtSearch.Services.TrackResult("youtube", "aaaaaaaaaaa", "One", "Daft Punk", 200, "", "");
        var b = new Jellyfin.Plugin.YtSearch.Services.TrackResult("youtube", "bbbbbbbbbbb", "Two", "Daft Punk - Topic", 200, "", "");
        Assert.Equal(a.ArtistId, b.ArtistId);
    }

    [Fact]
    public void TagsListEveryArtistAndTheFirstAlbumArtist()
    {
        var meta = new Jellyfin.Plugin.YtSearch.Services.TrackMeta("Get Lucky", "Daft Punk, Pharrell Williams", "RAM", "Daft Punk", 2013, 8, 1, "Pop");
        var t = T("x") with { Meta = meta };
        var tags = Jellyfin.Plugin.YtSearch.Services.AudioTagger.Tags(t).ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal("Daft Punk; Pharrell Williams", tags["artist"]);
        Assert.Equal("Daft Punk", tags["album_artist"]);
        Assert.Equal("RAM", tags["album"]);
    }

    [Fact]
    public void ArtistPageJsonHasTheArtistIdOfThatName()
    {
        var t = T("Daft Punk, Pharrell Williams");
        var json = Jellyfin.Plugin.YtSearch.Middleware.SearchAppendMiddleware.Artist(t, "Pharrell Williams", "srv");
        Assert.Equal("MusicArtist", (string)json["Type"]!);
        Assert.Equal(t.ArtistIdFor("Pharrell Williams").ToString("N"), (string)json["Id"]!);
    }

    [Fact]
    public void TrackJsonListsAllArtistsEachWithItsOwnId()
    {
        var t = T("Daft Punk, Pharrell Williams");
        var json = Jellyfin.Plugin.YtSearch.Middleware.SearchAppendMiddleware.Item(t, "srv");
        Assert.Equal(2, json["Artists"]!.AsArray().Count);
        Assert.Equal(2, json["ArtistItems"]!.AsArray().Count);
        Assert.Equal(t.ArtistIdFor("Pharrell Williams").ToString("N"), (string)json["ArtistItems"]![1]!["Id"]!);
        Assert.Equal("Daft Punk", (string)json["AlbumArtist"]!);
    }
}

public class DuplicateAndArtistSearchTests
{
    private static Jellyfin.Plugin.YtSearch.Services.TrackResult T(string src, string id, string title, string artist = "X", double dur = 213) =>
        new(src, id, title, artist, dur, "", "");

    private const string Yt = Jellyfin.Plugin.YtSearch.Services.Sources.YouTube;
    private const string Sc = Jellyfin.Plugin.YtSearch.Services.Sources.SoundCloud;

    private static System.Collections.Generic.List<string> Dedupe(Jellyfin.Plugin.YtSearch.Services.TrackResult[] all, params string[] downloaded) =>
        Jellyfin.Plugin.YtSearch.Services.SearchService.DedupeAcrossSources(all, r => downloaded.Contains(r.Key)).Select(r => r.Key).ToList();

    [Fact]
    public void SameSongOnBothSitesKeepsYouTubeWhenNothingIsDownloaded()
    {
        var all = new[] { T(Sc, "1", "Get Lucky"), T(Yt, "aaaaaaaaaaa", "Get Lucky") };
        Assert.Equal(new[] { "youtube:aaaaaaaaaaa" }, Dedupe(all));
    }

    [Fact]
    public void ADownloadedSoundCloudCopyWinsOverANewYouTubeOne()
    {
        var all = new[] { T(Sc, "1", "Get Lucky"), T(Yt, "aaaaaaaaaaa", "Get Lucky") };
        Assert.Equal(new[] { "soundcloud:1" }, Dedupe(all, "soundcloud:1"));
    }

    [Fact]
    public void ADownloadedYouTubeCopyWinsOverSoundCloud()
    {
        var all = new[] { T(Sc, "1", "Get Lucky"), T(Yt, "aaaaaaaaaaa", "Get Lucky") };
        Assert.Equal(new[] { "youtube:aaaaaaaaaaa" }, Dedupe(all, "youtube:aaaaaaaaaaa"));
    }

    [Fact]
    public void WhenBothAreDownloadedBothStay()
    {
        var all = new[] { T(Sc, "1", "Get Lucky"), T(Yt, "aaaaaaaaaaa", "Get Lucky") };
        Assert.Equal(2, Dedupe(all, "soundcloud:1", "youtube:aaaaaaaaaaa").Count);
    }

    [Fact]
    public void NameDifferencesLikeOfficialAudioArtistOrderAndFeaturingDontMatter()
    {
        Assert.True(Same("Daft Punk - Get Lucky (Official Audio) ft. Pharrell Williams", "Get Lucky - Daft Punk"));
        Assert.True(Same("Rick Astley - Never Gonna Give You Up (Official Video)", "Never Gonna Give You Up"));
        Assert.True(Same("GET LUCKY!", "get lucky"));
    }

    [Fact]
    public void RemixesCoversAndDifferentLengthsAreNotDuplicates()
    {
        Assert.False(Same("Get Lucky", "Get Lucky (Remix)"));
        Assert.False(Same("Get Lucky", "Get Lucky cover"));
        Assert.False(Same("Get Lucky", "Get Lucky live"));
        Assert.False(Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(T(Yt, "aaaaaaaaaaa", "Get Lucky", dur: 369), T(Sc, "1", "Get Lucky", dur: 36000)));
    }

    [Fact]
    public void ArtistDashSongTitlesMatchTheBareSongByTheSameArtist()
    {
        var d = Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(
            T(Yt, "aaaaaaaaaaa", "Cher - Believe", "Cher - Topic", 240), T(Sc, "1", "Believe", "Cher", 241));
        Assert.True(d);
        Assert.True(Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(
            T(Yt, "aaaaaaaaaaa", "Believe - Cher", "Some Uploader", 240), T(Sc, "1", "Cher - Believe", "Cher", 240)));
        // same one-word title, different artists: different songs
        Assert.False(Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(
            T(Yt, "aaaaaaaaaaa", "Cher - Believe", "Cher", 240), T(Sc, "1", "Believe", "Justin Bieber", 240)));
        Assert.True(Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(
            T(Yt, "aaaaaaaaaaa", "Daft Punk - One More Time", "Daft Punk - Topic", 320), T(Sc, "1", "One More Time", "Daft Punk", 320)));
    }

    [Fact]
    public void SongsOfDifferentLengthAreBothShown()
    {
        Assert.True(Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(T(Yt, "aaaaaaaaaaa", "Get Lucky", dur: 369), T(Sc, "1", "Get Lucky", dur: 372)));
        Assert.False(Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(T(Yt, "aaaaaaaaaaa", "Get Lucky", dur: 369), T(Sc, "1", "Get Lucky", dur: 248)));
        Assert.False(Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(T(Yt, "aaaaaaaaaaa", "Get Lucky", dur: 200), T(Sc, "1", "Get Lucky", dur: 215)));
    }

    [Fact]
    public void DifferentSongsAreNotDuplicates()
    {
        Assert.False(Same("Get Lucky", "Lose Yourself to Dance"));
        Assert.False(Same("Believe", "Believe Me")); // a one-word title never matches by subset
        // two or more shared words and the same length is treated as the same song (a title plus the artist name)
        Assert.Single(Dedupe(new[] { T(Sc, "1", "Love Story Taylor Swift", dur: 200), T(Yt, "aaaaaaaaaaa", "Love Story", dur: 200) }));
    }

    [Fact]
    public void CopiesOnTheSameSiteAreLeftAlone()
    {
        var all = new[] { T(Yt, "aaaaaaaaaaa", "Get Lucky"), T(Yt, "bbbbbbbbbbb", "Get Lucky"), T(Sc, "1", "Another Song"), T(Sc, "2", "Another Song") };
        Assert.Equal(4, Dedupe(all).Count);
    }

    private static bool Same(string a, string b) =>
        Jellyfin.Plugin.YtSearch.Services.SearchService.AreSameSong(T(Yt, "aaaaaaaaaaa", a), T(Sc, "1", b));

    [Fact]
    public void ArtistSongsKeepOnlyThoseByTheArtistAndNoLongMixes()
    {
        var results = new[]
        {
            T(Sc, "1", "Get Lucky", "Daft Punk"),
            T(Yt, "aaaaaaaaaaa", "Daft Punk - One More Time", "Some Fan Channel"),
            T(Yt, "bbbbbbbbbbb", "Harder Better Faster", "Daft Punk - Topic"),
            T(Yt, "ccccccccccc", "Totally Different Artist Song", "Other"),
            T(Yt, "ddddddddddd", "Daft Punk Megamix", "Daft Punk", 3600),
        };
        var kept = Jellyfin.Plugin.YtSearch.Services.SearchService.FilterArtistSongs("daft punk", results).Select(r => r.Key).ToList();
        Assert.Equal(new[] { "youtube:aaaaaaaaaaa", "youtube:bbbbbbbbbbb", "soundcloud:1" }, kept); // YouTube first
    }

    [Fact]
    public void ArtistSongsOfNothingIsEmpty() =>
        Assert.Empty(Jellyfin.Plugin.YtSearch.Services.SearchService.FilterArtistSongs("   ", new[] { T(Yt, "aaaaaaaaaaa", "x") }));
}

public class LibraryChoiceTests
{
    [Theory]
    [InlineData("/media/music", true)]
    [InlineData("/music", true)]
    [InlineData("/data/Music Library/Songs", true)]
    [InlineData("/var/lib/jellyfin/music", true)]
    [InlineData("/mnt/nas/music/", true)]
    [InlineData("/", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("music", false)]
    [InlineData("../music", false)]
    [InlineData("/media/../etc", false)]
    [InlineData("/etc", false)]
    [InlineData("/etc/jellyfin", false)]
    [InlineData("/usr/lib/x", false)]
    [InlineData("/proc/self", false)]
    [InlineData("/dev/shm", false)]
    [InlineData("/root/music", false)]
    [InlineData("/var", false)]
    [InlineData("/home", false)]
    [InlineData("/tmp", false)]
    [InlineData("/media/mu\u0007sic", false)]
    public void OnlySensibleFoldersCanBeTheLibrary(string? path, bool ok) =>
        Assert.Equal(ok, Jellyfin.Plugin.YtSearch.Services.LibraryService.IsAcceptableRoot(path));

    private static readonly System.Guid Lib = System.Guid.NewGuid();

    [Fact]
    public void AdminsAndAllFoldersUsersAreAllowedDisabledNever()
    {
        Assert.True(Jellyfin.Plugin.YtSearch.Services.LibraryService.Allows(true, false, false, new System.Guid[0], null));
        Assert.True(Jellyfin.Plugin.YtSearch.Services.LibraryService.Allows(false, false, true, new System.Guid[0], null));
        Assert.False(Jellyfin.Plugin.YtSearch.Services.LibraryService.Allows(true, true, true, new[] { Lib }, Lib));
    }

    [Fact]
    public void OtherUsersNeedTheLibraryEnabled()
    {
        Assert.True(Jellyfin.Plugin.YtSearch.Services.LibraryService.Allows(false, false, false, new[] { Lib }, Lib));
        Assert.False(Jellyfin.Plugin.YtSearch.Services.LibraryService.Allows(false, false, false, new[] { System.Guid.NewGuid() }, Lib));
        Assert.False(Jellyfin.Plugin.YtSearch.Services.LibraryService.Allows(false, false, false, new[] { Lib }, null));
    }

    [Theory]
    [InlineData("/media/music", "/media/music/yt-dQw4w9WgXcQ.m4a", true)]
    [InlineData("/media/music", "/media/music/Artist/Album/sc-123.m4a", true)]
    [InlineData("/media/music", "/media/music/Artist/Album/My Own Song.m4a", false)]
    [InlineData("/media/music", "/media/music/Artist/Album/Disc 1/yt-dQw4w9WgXcQ.m4a", false)]
    [InlineData("/media/music", "/media/music2/yt-dQw4w9WgXcQ.m4a", false)]
    [InlineData("/media/music", "/media/other/yt-dQw4w9WgXcQ.m4a", false)]
    [InlineData("/media/music", "/media/music/Artist/yt-dQw4w9WgXcQ.mp3", false)]
    public void CleanupOnlyEverTouchesFilesThePluginCreatedInTheChosenLibrary(string root, string path, bool ours) =>
        Assert.Equal(ours, Jellyfin.Plugin.YtSearch.Services.LibraryService.IsOurFile(root, path));

    private const string Ebur = "[Parsed_ebur128_0 @ 0x1] Summary:\n\n  Integrated loudness:\n    I:         -21.8 LUFS\n    Threshold: -31.8 LUFS\n\n  Loudness range:\n    LRA:         0.0 LU\n\n  True peak:\n    Peak:      -14.5 dBFS\n";

    [Fact]
    public void LoudnessIsReadFromTheEbur128SummaryAndBecomesAGain()
    {
        var l = AudioTagger.ParseLoudness("t: 0.1 M: -20 S: -20 I: -5.0 LUFS LRA: 0.0 LU\n" + Ebur);
        Assert.NotNull(l);
        Assert.Equal(-21.8, l!.Lufs);
        Assert.Equal(3.8, AudioTagger.GainFor(l));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no summary here")]
    [InlineData("Summary:\n  Integrated loudness:\n    I:         -inf LUFS\n  True peak:\n    Peak:      -inf dBFS\n")]
    public void SilenceOrGarbageGivesNoGain(string log)
    {
        Assert.Null(AudioTagger.ParseLoudness(log));
        Assert.Null(AudioTagger.GainFor(null));
    }

    [Fact]
    public void LyricsPreferTimedOverPlainAndSkipInstrumentals()
    {
        var timed = LyricsClient.ParseOne("{\"plainLyrics\":\"a\",\"syncedLyrics\":\"[00:01.00] a\"}");
        Assert.Equal((".lrc", "[00:01.00] a"), LyricsClient.ForSidecar(timed));
        Assert.Equal((".txt", "a"), LyricsClient.ForSidecar(LyricsClient.ParseOne("{\"plainLyrics\":\"a\",\"syncedLyrics\":null}")));
        Assert.Null(LyricsClient.ParseOne("{\"instrumental\":true,\"plainLyrics\":\"a\"}"));
        Assert.Null(LyricsClient.ParseOne("not json".Length > 0 ? "" : null));
        Assert.Null(LyricsClient.ForSidecar(null));
    }

    [Fact]
    public void LyricsSearchPicksTheClosestLengthWithinFiveSecondsPreferringTimed()
    {
        const string json = "[{\"duration\":200,\"plainLyrics\":\"far\"},{\"duration\":181,\"plainLyrics\":\"plain\"},{\"duration\":183,\"syncedLyrics\":\"[00:01.00] timed\"}]";
        Assert.Equal("[00:01.00] timed", LyricsClient.PickBest(json, 180)!.Synced);
        Assert.Null(LyricsClient.PickBest("[{\"duration\":200,\"plainLyrics\":\"far\"}]", 180));
    }

    [Fact]
    public void SourceInfoGivesYearAndGenreOnlyWhereTheCatalogHasNone()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"release_year\":1998,\"genres\":[\"Pop\"],\"genre\":\"Other\"}");
        var info = AudioTagger.ParseSourceInfo(doc.RootElement);
        Assert.Equal(1998, info.Year);
        Assert.Equal("Pop", info.Genre);
        using var d2 = System.Text.Json.JsonDocument.Parse("{\"release_date\":\"20110203\",\"genre\":\"Hip-hop\\u0007\"}");
        Assert.Equal(2011, AudioTagger.ParseSourceInfo(d2.RootElement).Year);
        Assert.Equal("Hip-hop", AudioTagger.ParseSourceInfo(d2.RootElement).Genre);
        using var d3 = System.Text.Json.JsonDocument.Parse("{\"release_year\":12,\"genre\":\"\"}");
        Assert.Null(AudioTagger.ParseSourceInfo(d3.RootElement).Year);
        Assert.Null(AudioTagger.ParseSourceInfo(d3.RootElement).Genre);
        // a missing file is no information, not an error
        Assert.Null(AudioTagger.ReadSourceInfo("/nonexistent/audio.info.json").Year);
    }

    [Theory]
    [InlineData("Daft Punk - Daft Club (Official Album Playlist)", "Daft Punk", "Daft Club")]
    [InlineData("Random Access Memories (Full Album)", "Daft Punk", "Random Access Memories")]
    [InlineData("Discovery - Daft Punk (Official Album Playlist)", "Daft Punk", "Discovery")]
    public void AlbumNamesAreTakenFromPlaylistTitles(string title, string artist, string expected) =>
        Assert.Equal(expected, ArtistProfileService.AlbumName(title, artist));

    [Theory]
    [InlineData("Daft Punk - Daft Club (Official Album Playlist)", true)]
    [InlineData("Some Band - Debut (Full Album)", true)]
    [InlineData("Daft Punk Experience x Fortnite", false)]
    [InlineData("Random Access Memories - Memory Tapes", false)]
    [InlineData("Daft Punk - Human After All (Remixes) (Official Album Playlist)", false)]
    public void OnlyAlbumPlaylistsAreTreatedAsAlbums(string title, bool album) =>
        Assert.Equal(album, ArtistProfileService.IsAlbumPlaylist(title));

    [Fact]
    public void TrackTitlesLoseTheArtistAndOfficialAudioWording()
    {
        Assert.Equal("Aerodynamite", ArtistProfileService.CleanTrackTitle("Daft Punk - Aerodynamite (Official Audio)", "Daft Punk"));
        Assert.Equal("One More Time", ArtistProfileService.CleanTrackTitle("One More Time [Official Video]", "Daft Punk"));
        Assert.Equal("Robot Rock (Maximum Overdrive Mix)", ArtistProfileService.CleanTrackTitle("Robot Rock (Maximum Overdrive Mix) (Official Audio)", "Daft Punk"));
        Assert.Equal("Da Funk", ArtistProfileService.CleanTrackTitle("Da Funk (Official Music Video Remastered)", "Daft Punk"));
    }

    [Fact]
    public void OnlyRealSongsAreTaken()
    {
        Assert.True(ArtistProfileService.IsSong("Get Lucky", 248));
        Assert.False(ArtistProfileService.IsSong("Get Lucky (Remix)", 248));
        Assert.False(ArtistProfileService.IsSong("Daft Punk live in Paris", 248));
        Assert.False(ArtistProfileService.IsSong("Best of Daft Punk mix", 248));
        Assert.False(ArtistProfileService.IsSong("Teaser", 20));
        Assert.False(ArtistProfileService.IsSong("Daft Punk 3 hours", 10800));
        Assert.False(ArtistProfileService.IsSong("[Private video]", 200));
        Assert.False(ArtistProfileService.IsSong("10 Years Of Random Access Memories", 200));
        Assert.False(ArtistProfileService.IsSong("Random Access Memories (10th Anniversary Edition) Announcement", 200));
    }

    [Fact]
    public void TheOfficialChannelIsTheFirstExactNameThatIsNotATopicChannel()
    {
        var results = new[]
        {
            new Jellyfin.Plugin.YtSearch.Services.YtDlpService.FlatEntry("UCRr1xG_2WIDs18a6cIiCxeA", "Daft Punk - Topic", "", 0, "", ""),
            new Jellyfin.Plugin.YtSearch.Services.YtDlpService.FlatEntry("UCAl4zrrhZFYrHamdEKSCUKg", "Daft Funk Live", "", 0, "", ""),
            new Jellyfin.Plugin.YtSearch.Services.YtDlpService.FlatEntry("UC_kRDKYrUlrbtrSiyu5Tflg", "Daft Punk", "", 0, "", ""),
        };
        Assert.Equal("UC_kRDKYrUlrbtrSiyu5Tflg", ArtistProfileService.PickChannel(results, "Daft Punk"));
        Assert.Null(ArtistProfileService.PickChannel(results, "Someone Else"));
    }

    [Fact]
    public void ProfilesMergeSoSongsOnAlbumsOrOnBothSitesAreListedOnce()
    {
        var album = new ArtistProfileService.ProfileAlbum("Discovery", new[]
        {
            T("youtube", "aaaaaaaaaaa", "One More Time"),
            T("youtube", "bbbbbbbbbbb", "Aerodynamic"),
        });
        var yt = new[] { T("youtube", "aaaaaaaaaaa", "One More Time"), T("youtube", "ccccccccccc", "Get Lucky (Official Video)") };
        var sc = new[] { T("soundcloud", "1", "Get Lucky"), T("soundcloud", "2", "Aerodynamic"), T("soundcloud", "3", "Other Song") };
        var merged = ArtistProfileService.Assemble(new[] { album, album }, yt, sc);
        Assert.Single(merged.Albums);
        Assert.Equal(new[] { "Get Lucky (Official Video)", "Other Song" }, merged.Loose.Select(t => t.DisplayTitle).ToArray());
    }

    [Fact]
    public void FlatListingsAreParsed()
    {
        var list = Jellyfin.Plugin.YtSearch.Services.YtDlpService.ParseFlat("{\"title\":\"Daft Punk - Daft Club\",\"channel\":\"Daft Punk\",\"entries\":[{\"id\":\"HU7KlDJVINc\",\"title\":\"Daft Punk - Ouverture\",\"duration\":161,\"channel\":\"Daft Punk\"},{\"title\":\"no id\"}]}");
        Assert.NotNull(list);
        Assert.Single(list!.Entries);
        Assert.Equal(161, list.Entries[0].Seconds);
        Assert.Null(Jellyfin.Plugin.YtSearch.Services.YtDlpService.ParseFlat("not json"));
    }

    [Fact]
    public void SoundCloudProfileIsTheMostFollowedExactNameMatch()
    {
        const string json = "{\"collection\":[{\"username\":\"Deede Collective Fans\",\"permalink_url\":\"https://soundcloud.com/fans\",\"followers_count\":900},{\"username\":\"Deede Collective\",\"permalink_url\":\"https://soundcloud.com/deedecollective\",\"followers_count\":50},{\"username\":\"deede collective\",\"permalink_url\":\"https://soundcloud.com/other\",\"followers_count\":10}]}";
        var p = OnlineSearchClient.ParseSoundCloudProfile(json, "Deede Collective");
        Assert.Equal("https://soundcloud.com/deedecollective", p!.Value.Url);
        Assert.Null(OnlineSearchClient.ParseSoundCloudProfile(json, "Nobody"));
    }

    private static Jellyfin.Plugin.YtSearch.Services.TrackResult T(string src, string id, string title) => new(src, id, title, "Daft Punk", 200, "", "");

    private const string AlbumLookup = """
    {"resultCount":7,"results":[
      {"wrapperType":"artist","artistName":"Drake","artistId":271256},
      {"wrapperType":"collection","collectionType":"Album","collectionId":1,"collectionName":"Scorpion","artistName":"Drake","releaseDate":"2018-06-29T07:00:00Z","artworkUrl100":"https://is1-ssl.mzstatic.com/a/100x100bb.jpg","primaryGenreName":"Hip-Hop/Rap"},
      {"wrapperType":"collection","collectionType":"Album","collectionId":2,"collectionName":"Take Care (Deluxe Version)","artistName":"Drake","releaseDate":"2011-11-15T08:00:00Z"},
      {"wrapperType":"collection","collectionType":"Album","collectionId":3,"collectionName":"Her Loss","artistName":"Drake & 21 Savage","releaseDate":"2022-11-04T07:00:00Z"},
      {"wrapperType":"collection","collectionType":"Album","collectionId":4,"collectionName":"God's Plan - Single","artistName":"Drake","releaseDate":"2018-01-19T08:00:00Z"},
      {"wrapperType":"collection","collectionType":"Album","collectionId":5,"collectionName":"Scorpion (Deluxe)","artistName":"Drake","releaseDate":"2018-07-01T07:00:00Z"},
      {"wrapperType":"collection","collectionType":"Album","collectionId":6,"collectionName":"Some Compilation","artistName":"Various Artists","releaseDate":"2020-01-01T07:00:00Z"}
    ]}
    """;

    [Fact]
    public void CatalogAlbumsAreTheDiscographyWithoutSinglesOrRepeats()
    {
        var albums = CatalogClient.ParseAlbums(AlbumLookup, "Drake");
        Assert.Equal(new[] { "Her Loss", "Scorpion", "Take Care" }, albums.Select(a => a.Name).ToArray());
        Assert.Equal(1, albums.First(a => a.Name == "Scorpion").CollectionId); // the standard edition wins over the deluxe one
        Assert.Equal("https://is1-ssl.mzstatic.com/a/600x600bb.jpg", albums.First(a => a.Name == "Scorpion").ArtworkUrl);
        Assert.Equal(2011, albums.First(a => a.Name == "Take Care").Year);
    }

    [Fact]
    public void CatalogArtistIsFoundByExactName()
    {
        Assert.Equal(271256, CatalogClient.ParseArtistId(AlbumLookup, "drake"));
        Assert.Equal("Drake", CatalogClient.ParseArtistName(AlbumLookup, "DRAKE"));
        Assert.Null(CatalogClient.ParseArtistName(AlbumLookup, "Drake Maye"));
    }

    [Fact]
    public void CatalogSongsKeepTheirIdsInAlbumOrder()
    {
        const string json = "{\"results\":[{\"wrapperType\":\"collection\"},{\"kind\":\"song\",\"trackId\":22,\"trackName\":\"B\",\"artistName\":\"X\",\"collectionName\":\"A\",\"trackNumber\":2,\"discNumber\":1,\"trackTimeMillis\":200000,\"collectionId\":9},{\"kind\":\"song\",\"trackId\":21,\"trackName\":\"A\",\"artistName\":\"X\",\"collectionName\":\"A\",\"trackNumber\":1,\"discNumber\":1,\"trackTimeMillis\":100000,\"collectionId\":9}]}";
        var songs = CatalogClient.ParseAlbumSongs(json);
        Assert.Equal(new long[] { 21, 22 }, songs.Select(s => s.TrackId).ToArray());
        Assert.Equal(9, songs[0].CollectionId);
        Assert.Equal(100, songs[0].DurationSeconds);
    }

    [Theory]
    [InlineData("God's Plan", "Drake", 199, "Drake - God's Plan (Official Audio)", "Drake - Topic", 199, true)]
    [InlineData("God's Plan", "Drake", 199, "God's Plan", "Drake", 201, true)]
    [InlineData("God's Plan", "Drake", 199, "God's Plan (Remix)", "Drake", 199, false)]
    [InlineData("God's Plan", "Drake", 199, "God's Plan live", "Drake", 199, false)]
    [InlineData("God's Plan", "Drake", 199, "God's Plan", "Drake", 260, false)]
    [InlineData("God's Plan", "Drake", 199, "God's Plan", "Some Unrelated Channel", 199, false)]
    [InlineData("God's Plan", "Drake", 199, "Drake - God's Plan reaction", "Drake", 199, false)]
    public void OnlyRealCopiesOfACatalogSongAreSources(string title, string artist, double seconds, string candidateTitle, string channel, double candidateSeconds, bool expected)
    {
        var wanted = new TrackResult(Sources.Catalog, "1", title, artist, seconds, "", "") { Meta = new TrackMeta(title, artist, "Scorpion", artist, 2018, 5, 1, null) };
        var candidate = new TrackResult(Sources.YouTube, "aaaaaaaaaaa", candidateTitle, channel, candidateSeconds, "", "https://www.youtube.com/watch?v=aaaaaaaaaaa");
        Assert.Equal(expected, SearchService.IsSourceFor(wanted, candidate));
    }

    [Fact]
    public void CatalogIdsAreNumbersAndAlbumsStandInForSongs()
    {
        Assert.True(InputGuard.IsValidSourceId(Sources.Catalog, "1418213269"));
        Assert.True(InputGuard.IsValidSourceId(Sources.Catalog, "a1418213110"));
        Assert.False(InputGuard.IsValidSourceId(Sources.Catalog, "../../x"));
        Assert.True(new TrackResult(Sources.Catalog, "a12", "A", "X", 0, "", "").IsAlbumStub);
        Assert.False(new TrackResult(Sources.Catalog, "12", "A", "X", 0, "", "").IsAlbumStub);
        Assert.True(LibraryService.IsOurFile("/m", "/m/Drake/Scorpion/ct-1418213269.m4a"));
    }
}
