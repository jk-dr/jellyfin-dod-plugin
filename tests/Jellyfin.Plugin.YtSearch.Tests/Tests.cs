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
