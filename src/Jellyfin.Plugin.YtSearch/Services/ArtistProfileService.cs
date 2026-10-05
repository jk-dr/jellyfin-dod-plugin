using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Fills an artist's page from the artist's own profiles, read with yt-dlp: the albums (official album playlists) and videos of
/// their YouTube channel, then the tracks of their SoundCloud profile. What the library already has is left out; songs missing
/// from an album the library has are offered for that album. Everything is an ordinary YouTube or SoundCloud track, so playing
/// one downloads it the usual way (YouTube preferred, then SoundCloud).
/// </summary>
public class ArtistProfileService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);
    private static readonly TimeSpan EmptyCacheFor = TimeSpan.FromMinutes(10);
    private const int MaxAlbumPlaylists = 12;
    private const int MaxVideos = 60;
    private const double MaxSongSeconds = 15 * 60;
    private const double MinSongSeconds = 45;

    private static readonly Regex ChannelId = new("^UC[A-Za-z0-9_-]{22}$", RegexOptions.Compiled);
    private static readonly Regex PlaylistId = new("^[A-Za-z0-9_-]{10,64}$", RegexOptions.Compiled);
    private static readonly Regex AlbumPlaylistTitle = new(@"official album playlist|full album|\(album\)|\[album\]|\balbum playlist\b|\(ep\)|\[ep\]|\bthe album\b|- ep$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotAMusicPlaylist = new(@"remix|live|making of|behind the|drumless|instrumental|karaoke|tour|interview|trailer|experience|reaction|tutorial|session|acoustic|demo", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AlbumTitleNoise = new(@"\(\s*official\s+album\s+playlist\s*\)|\[\s*official\s+album\s+playlist\s*\]|official\s+album\s+playlist|\(\s*full\s+album\s*\)|\[\s*full\s+album\s*\]|full\s+album|\(\s*album\s*\)|\[\s*album\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrackTitleNoise = new(@"\s*[\(\[]\s*(?:official\s+)?(?:music\s+video|video|audio|lyric\s+video|lyrics?|visuali[sz]er|hd|4k)(?:\s+remastered)?\s*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NotASong = new(@"\b(full album|mix|compilation|playlist|trailer|interview|behind the scenes|making of|live|reaction|tutorial|podcast|episode|announcement|teaser|anniversary|documentary|unboxing|premiere|statement|years? of|edition|snippet|preview)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracketGroups = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex FeaturingTail = new(@"\s(?:feat\.?|ft\.?|featuring|with)\s.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly YtDlpService _ytdlp;
    private readonly OnlineSearchClient _online;
    private readonly LibraryService _library;
    private readonly TrackRegistry _registry;
    private readonly ILogger<ArtistProfileService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Expires, Task<Profile> Task)> _cache = new();
    private readonly object _lock = new();

    public ArtistProfileService(YtDlpService ytdlp, OnlineSearchClient online, LibraryService library, TrackRegistry registry, ILogger<ArtistProfileService> logger)
    {
        _ytdlp = ytdlp;
        _online = online;
        _library = library;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>An album of the artist's profile; the tracks are in album order and carry their album, track number and title.</summary>
    public sealed record ProfileAlbum(string Name, IReadOnlyList<TrackResult> Tracks);

    /// <summary>What an artist's profiles hold: albums, and songs that are not on an album.</summary>
    public sealed record Profile(IReadOnlyList<ProfileAlbum> Albums, IReadOnlyList<TrackResult> Loose)
    {
        public static readonly Profile Empty = new(Array.Empty<ProfileAlbum>(), Array.Empty<TrackResult>());

        public bool IsEmpty => Albums.Count == 0 && Loose.Count == 0;
    }

    /// <summary>The profile's songs sorted into: whole albums the library lacks, gaps in albums it has, and loose songs.</summary>
    public sealed record Placement(IReadOnlyList<IReadOnlyList<TrackResult>> NewAlbums, IReadOnlyList<TrackResult> Gaps, IReadOnlyList<TrackResult> Loose)
    {
        public IEnumerable<TrackResult> AllSongs => NewAlbums.SelectMany(a => a).Concat(Gaps).Concat(Loose);

        /// <summary>One track per missing album, standing for the album.</summary>
        public IEnumerable<TrackResult> AlbumRepresentatives => NewAlbums.Where(a => a.Count > 0).Select(a => a[0]);
    }

    public static bool Enabled => Plugin.Instance?.Configuration.UseArtistProfiles ?? true;

    /// <summary>
    /// The artist's songs the library does not have yet, or null when the profiles have not been read within
    /// <paramref name="budget"/> (the reading goes on in the background and the next request is served from the cache).
    /// </summary>
    public async Task<Placement?> ForArtistAsync(string artist, Guid? artistId, TimeSpan budget, CancellationToken ct)
    {
        if (!Enabled || _library.TryRoot is null)
        {
            return null;
        }

        Profile profile;
        try
        {
            profile = await GetProfile(artist).WaitAsync(budget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }

        var albums = artistId is { } id ? _library.AlbumsOfArtist(id) : Array.Empty<(string Name, string Folder, IReadOnlyList<string> Titles)>();
        var titles = artistId is { } id2 ? _library.SongTitlesOfArtist(id2) : Array.Empty<string>();
        var placement = Place(profile, albums, titles);
        _registry.Add(placement.AllSongs.ToList());
        return placement;
    }

    /// <summary>Songs the profile has for an album the library already has but that are missing from it.</summary>
    public async Task<IReadOnlyList<TrackResult>> MissingForAlbumAsync(Guid albumId, TimeSpan budget, CancellationToken ct)
    {
        if (!Enabled || _library.TryRoot is null || _library.AlbumContents(albumId) is not { } album || album.Artist.Length == 0)
        {
            return Array.Empty<TrackResult>();
        }

        Profile profile;
        try
        {
            profile = await GetProfile(album.Artist).WaitAsync(budget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Array.Empty<TrackResult>();
        }

        var match = profile.Albums.FirstOrDefault(a => NamesMatch(a.Name, album.Name));
        if (match is null)
        {
            return Array.Empty<TrackResult>();
        }

        var gaps = Gaps(match, (album.Name, album.Folder, album.Titles));
        _registry.Add(gaps);
        return gaps;
    }

    private Task<Profile> GetProfile(string artist)
    {
        var key = artist.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var entry) && entry.Expires >= DateTime.UtcNow)
            {
                return entry.Task;
            }

            // Not tied to a request: a client that gives up does not stop the reading. BuildAsync never fails.
            var task = BuildAsync(artist);
            _cache[key] = (DateTime.UtcNow + CacheFor, task);
            _ = task.ContinueWith(
                t =>
                {
                    if (t.Result.IsEmpty)
                    {
                        lock (_lock)
                        {
                            _cache[key] = (DateTime.UtcNow + EmptyCacheFor, t);
                        }
                    }
                },
                TaskScheduler.Default);
            foreach (var old in _cache.Where(kv => kv.Value.Expires < DateTime.UtcNow).Select(kv => kv.Key).ToList())
            {
                _cache.TryRemove(old, out _);
            }

            return task;
        }
    }

    private async Task<Profile> BuildAsync(string artist)
    {
        try
        {
            var ytTask = BuildYouTubeAsync(artist);
            var scTask = BuildSoundCloudAsync(artist);
            await Task.WhenAll(ytTask, scTask).ConfigureAwait(false);
            var (albums, videos) = ytTask.Result;
            var profile = Assemble(albums, videos, scTask.Result);
            _logger.LogInformation("Profile of '{Artist}': {Albums} albums, {Loose} other songs", artist, profile.Albums.Count, profile.Loose.Count);
            return profile;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the profiles of '{Artist}' failed", artist);
            return Profile.Empty;
        }
    }

    private async Task<(List<ProfileAlbum> Albums, List<TrackResult> Videos)> BuildYouTubeAsync(string artist)
    {
        var albums = new List<ProfileAlbum>();
        var videos = new List<TrackResult>();
        var channel = await FindChannelAsync(artist).ConfigureAwait(false);
        if (channel is null)
        {
            return (albums, videos);
        }

        var playlistsTask = _ytdlp.ListAsync($"https://www.youtube.com/channel/{channel}/playlists", 80, CancellationToken.None);
        var videosTask = _ytdlp.ListAsync($"https://www.youtube.com/channel/{channel}/videos", MaxVideos, CancellationToken.None);
        var playlists = (await playlistsTask.ConfigureAwait(false))?.Entries
            .Where(e => PlaylistId.IsMatch(e.Id) && IsAlbumPlaylist(e.Title))
            .Take(MaxAlbumPlaylists)
            .ToList() ?? new List<YtDlpService.FlatEntry>();

        var lists = await Task.WhenAll(playlists.Select(p => _ytdlp.ListAsync($"https://www.youtube.com/playlist?list={p.Id}", 80, CancellationToken.None))).ConfigureAwait(false);
        for (var i = 0; i < playlists.Count; i++)
        {
            var name = AlbumName(playlists[i].Title, artist);
            if (lists[i] is not { } list || name.Length == 0 || MetadataMatcher.IsSpecialEditionName(name))
            {
                continue;
            }

            var tracks = new List<TrackResult>();
            foreach (var e in list.Entries)
            {
                if (!InputGuard.IsValidSourceId(Sources.YouTube, e.Id) || IsUnavailable(e.Title))
                {
                    continue;
                }

                var title = CleanTrackTitle(e.Title, artist);
                tracks.Add(YouTubeTrack(e, artist, title) with
                {
                    Meta = new TrackMeta(title, artist, name, artist, null, tracks.Count + 1, 1, null),
                });
            }

            if (tracks.Count > 0)
            {
                albums.Add(new ProfileAlbum(name, tracks));
            }
        }

        var listing = await videosTask.ConfigureAwait(false);
        foreach (var e in listing?.Entries ?? new List<YtDlpService.FlatEntry>())
        {
            if (InputGuard.IsValidSourceId(Sources.YouTube, e.Id) && IsSong(e.Title, e.Seconds))
            {
                videos.Add(YouTubeTrack(e, artist, CleanTrackTitle(e.Title, artist)));
            }
        }

        return (albums, videos);
    }

    private async Task<List<TrackResult>> BuildSoundCloudAsync(string artist)
    {
        var tracks = new List<TrackResult>();
        if (!(Plugin.Instance?.Configuration.EnableSoundCloud ?? true))
        {
            return tracks;
        }

        var profile = await _online.FindSoundCloudProfileAsync(artist, CancellationToken.None).ConfigureAwait(false);
        if (profile is null)
        {
            return tracks;
        }

        var listing = await _ytdlp.ListAsync(profile.Value.Url + "/tracks", MaxVideos, CancellationToken.None).ConfigureAwait(false);
        foreach (var e in listing?.Entries ?? new List<YtDlpService.FlatEntry>())
        {
            if (!InputGuard.IsValidSourceId(Sources.SoundCloud, e.Id) || !IsSong(e.Title, e.Seconds) || InputGuard.SafePageUrl(e.Url) is not { } page)
            {
                continue;
            }

            tracks.Add(new TrackResult(Sources.SoundCloud, e.Id, CleanTrackTitle(e.Title, artist), artist, e.Seconds, string.Empty, page));
        }

        return tracks;
    }

    /// <summary>The artist's official YouTube channel: the first channel result named exactly like the artist.</summary>
    private async Task<string?> FindChannelAsync(string artist)
    {
        var list = await _ytdlp.ListAsync($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(artist)}&sp=EgIQAg%253D%253D", 10, CancellationToken.None).ConfigureAwait(false);
        return PickChannel(list?.Entries ?? new List<YtDlpService.FlatEntry>(), artist);
    }

    internal static string? PickChannel(IEnumerable<YtDlpService.FlatEntry> results, string artist)
    {
        var wanted = TitleKey(artist);
        foreach (var e in results)
        {
            var id = ChannelId.IsMatch(e.Id) ? e.Id : ChannelId.IsMatch(e.ChannelId) ? e.ChannelId : null;
            if (id is not null && wanted.Count > 0 && TitleKey(e.Title).SetEquals(wanted) && !e.Title.EndsWith("Topic", StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    private static TrackResult YouTubeTrack(YtDlpService.FlatEntry e, string artist, string title) =>
        new(Sources.YouTube, e.Id, title, artist, e.Seconds, $"https://i.ytimg.com/vi/{e.Id}/hqdefault.jpg", $"https://www.youtube.com/watch?v={e.Id}");

    internal static bool IsAlbumPlaylist(string title) => AlbumPlaylistTitle.IsMatch(title) && !NotAMusicPlaylist.IsMatch(title);

    private static bool IsUnavailable(string title) =>
        title.StartsWith("[Private video]", StringComparison.OrdinalIgnoreCase) || title.StartsWith("[Deleted video]", StringComparison.OrdinalIgnoreCase) || title.Length == 0;

    /// <summary>A real, standalone song: the right length, and not a mix, a live set, a remix or a cover.</summary>
    internal static bool IsSong(string title, double seconds) =>
        title.Length > 0 && !IsUnavailable(title)
        && (seconds <= 0 || (seconds >= MinSongSeconds && seconds <= MaxSongSeconds))
        && !NotASong.IsMatch(title)
        && !RelevanceRanker.Tokens(title).Any(RelevanceRanker.IsNoiseWord);

    /// <summary>"Daft Punk - Daft Club (Official Album Playlist)" becomes "Daft Club".</summary>
    internal static string AlbumName(string playlistTitle, string artist)
    {
        var name = AlbumTitleNoise.Replace(playlistTitle, string.Empty).Trim(' ', '-', '–', '—');
        name = AlbumPolicy.StripArtistPrefix(name, new[] { artist });
        foreach (var dash in new[] { " - ", " – ", " — " })
        {
            if (name.EndsWith(dash + artist, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^(dash.Length + artist.Length)];
            }
        }

        return InputGuard.CleanText(name.Trim(' ', '-', '–', '—'), 200, string.Empty);
    }

    /// <summary>"Daft Punk - Aerodynamite (Official Audio)" becomes "Aerodynamite".</summary>
    internal static string CleanTrackTitle(string title, string artist)
    {
        var cleaned = TrackTitleNoise.Replace(title, string.Empty).Trim();
        cleaned = AlbumPolicy.StripArtistPrefix(cleaned, new[] { artist }).Trim();
        return cleaned.Length > 0 ? cleaned : title;
    }

    /// <summary>Joins the albums and songs of both profiles into one profile, dropping repeats.</summary>
    internal static Profile Assemble(IReadOnlyList<ProfileAlbum> albums, IReadOnlyList<TrackResult> youTubeSongs, IReadOnlyList<TrackResult> soundCloudSongs)
    {
        var onAlbums = albums.SelectMany(a => a.Tracks).Select(t => t.SourceId).ToHashSet();
        var albumTitles = albums.SelectMany(a => a.Tracks).Select(t => TitleKey(t.DisplayTitle)).ToList();
        var seen = new List<HashSet<string>>(albumTitles);
        var loose = new List<TrackResult>();
        foreach (var t in youTubeSongs.Concat(soundCloudSongs))
        {
            var key = TitleKey(t.DisplayTitle);
            if (key.Count == 0 || onAlbums.Contains(t.SourceId) || seen.Any(k => KeysMatch(k, key)))
            {
                continue;
            }

            seen.Add(key);
            loose.Add(t);
        }

        // Albums that are the same (the profile lists a record twice) are kept once.
        var uniqueAlbums = new List<ProfileAlbum>();
        foreach (var a in albums)
        {
            if (!uniqueAlbums.Any(u => NamesMatch(u.Name, a.Name)))
            {
                uniqueAlbums.Add(a);
            }
        }

        return new Profile(uniqueAlbums, loose);
    }

    private Placement Place(Profile profile, IReadOnlyList<(string Name, string Folder, IReadOnlyList<string> Titles)> libraryAlbums, IReadOnlyList<string> libraryTitles)
    {
        var newAlbums = new List<IReadOnlyList<TrackResult>>();
        var gaps = new List<TrackResult>();
        var haveTitles = libraryTitles.Concat(libraryAlbums.SelectMany(a => a.Titles)).Select(TitleKey).ToList();
        foreach (var album in profile.Albums)
        {
            var existing = libraryAlbums.FirstOrDefault(a => NamesMatch(a.Name, album.Name));
            if (existing.Name is not null)
            {
                gaps.AddRange(Gaps(album, existing));
                continue;
            }

            var tracks = album.Tracks.Select(_library.WithId).Where(t => !_library.IsPromoted(t.TrackId)).ToList();
            if (tracks.Count > 0)
            {
                newAlbums.Add(tracks);
            }
        }

        var loose = profile.Loose
            .Where(t => !haveTitles.Any(k => KeysMatch(k, TitleKey(t.DisplayTitle))))
            .Select(t => _library.WithId(t with { Meta = AlbumPolicy.For(t, null) }))
            .Where(t => !_library.IsPromoted(t.TrackId))
            .ToList();
        return new Placement(newAlbums, gaps, loose);
    }

    /// <summary>The tracks of a profile album that an album of the library lacks, placed into that album's folder.</summary>
    private List<TrackResult> Gaps(ProfileAlbum album, (string Name, string Folder, IReadOnlyList<string> Titles) existing)
    {
        var have = existing.Titles.Select(TitleKey).ToList();
        return album.Tracks
            .Where(t => !have.Any(k => KeysMatch(k, TitleKey(t.DisplayTitle))))
            .Select(t => _library.WithId(t with { Meta = t.Meta! with { Album = existing.Name }, FolderOverride = existing.Folder }))
            .Where(t => !_library.IsPromoted(t.TrackId))
            .ToList();
    }

    /// <summary>The words of a song title without brackets, "feat." parts and filler words, for comparing titles.</summary>
    internal static HashSet<string> TitleKey(string? title)
    {
        var text = BracketGroups.Replace(FeaturingTail.Replace(title ?? string.Empty, string.Empty), " ");
        return RelevanceRanker.Tokens(text).Where(w => !MetadataMatcher.IsFillerWord(w)).ToHashSet();
    }

    internal static bool KeysMatch(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        if (a.SetEquals(b))
        {
            return true;
        }

        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        return small.Count >= 2 && small.IsSubsetOf(large);
    }

    internal static bool NamesMatch(string a, string b) => KeysMatch(TitleKey(a), TitleKey(b));
}
