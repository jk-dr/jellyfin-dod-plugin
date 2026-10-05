# Jellyfin YouTube + SoundCloud search plugin (personal use)

Appends YouTube and SoundCloud results to Jellyfin's standard search API (`/Items?searchTerm=`,
`/Search/Hints`), so any Jellyfin client shows them as normal songs. Target: Jellyfin 10.11.11 (.NET 9).

## How it works

- **Search:** YouTube and SoundCloud are queried directly over HTTP (about 0.5 s; yt-dlp is the automatic fallback),
  merged and ranked by relevance to what you typed, and appended to the JSON of `/Items?searchTerm=` and
  `/Search/Hints`. Result ids are the ids Jellyfin will give the files, so they never change.
- **Volume levelling:** each download is measured with ffmpeg (about 2-3 s for a 4-minute song) and the volume change
  that brings it to the standard level (-18 LUFS) is stored on the Jellyfin item (its normalisation gain), which
  Jellyfin's own volume levelling uses. The audio is never re-encoded and the file keeps ordinary tags. Setting:
  "Level the volume of downloads".
- **Timed lyrics:** while the audio downloads, the song is looked up on lrclib.net (artist, title, album and length
  are sent there; 6 s limit, failures are ignored). Timed lyrics are saved as `yt-ID.lrc` (or `.txt` when only
  plain lyrics exist) next to the song, where Jellyfin reads them. Cleanup deletes them with the song. Setting:
  "Fetch timed lyrics".
- **Real albums:** each result is matched against Apple's iTunes Search (no key) for its real album, track number,
  year and cover. Only confident matches get an album (no remixes, covers or live versions); the rest become an album named after the song under their artist.
- **First use:** when a client plays, downloads, favorites or adds a result to a playlist, the plugin downloads the
  m4a, writes the tags and cover into it with ffmpeg stream copy (no re-encoding), files it as
  `Artist/Album/yt-ID.m4a`, and has Jellyfin's own scanner index it. Albums, artists, artwork
  and "recently added" then work like any other music.
- **Cleanup:** after each new download (and daily at 04:00) tracks played at most 3 times, not a favorite of any
  user, in no playlist and untouched for 7 days are deleted, with their empty album/artist folders. Their ids keep
  working (they download again on demand).
- **yt-dlp:** the plugin downloads its own nightly binary (checksum-verified) and updates it every 12 hours.
- **Cookies:** upload a Netscape `cookies.txt` on the plugin settings page (age-restricted / bot-check videos);
  the Refresh button checks that they still work. Cookies are only sent to YouTube.
- **Choosing the library:** on the settings page, pick which of your existing music libraries
  downloads go into (or another folder inside one). The plugin never creates a library: with nothing chosen it uses
  the first writable music library Jellyfin has, and with none it shows no online results. Songs are
  saved as `Artist/Album/` folders in it. Cleanup only ever deletes files the plugin created (`yt-*.m4a`,
  `sc-*.m4a`) and only the folders those left empty; the size limit counts only those files, and metadata of
  albums/artists you already have is never overwritten. System folders (`/`, `/etc`, `/usr`, ...) are refused.
  Users only get online results if they may use that library (administrators and users with all libraries do).
- **Same song on both sites:** if a song is on YouTube and SoundCloud with the same name, only one is shown: the
  one you already downloaded, otherwise the YouTube one. "Artist - Song" and "Song - Artist" titles count as the song's name. Copies whose lengths
  differ by more than a few seconds (3%) are different recordings and both stay. Copies on the same site are left alone.
- **Artists:** several artists in a credit ("A, B & C") each get their own artist; " - Topic" is dropped from channel
  names. Opening an artist adds that artist's songs from the sites; artists of search results have a page too.
- **Artist and album folders:** every song is filed as `Artist/Album/yt-ID.m4a` inside the library, and the folders are
  created when the song is added. A song with no known album gets its own album named after the song (`Title`) under its artist.
  There is no "one album for everything" mode.
- **Artists, albums and songs you don't have yet:** searching an artist's name (or opening them) fills the Songs, Albums
  and Artists sections, not just songs. Two sources are combined:
  - the artist's own profiles, read with yt-dlp: the official YouTube channel (its "Official Album Playlist" albums and
    its videos) and the SoundCloud profile, whose songs are real YouTube/SoundCloud tracks;
  - the iTunes catalog (no key): the artist's full discography with track lists, track numbers and cover art. Opening
    one of these albums lists all its songs. Clicking a song looks for a matching upload (the right song and length, no
    remixes, covers or live versions): the artist's best YouTube copies first, then SoundCloud ones, moving on if a copy
    fails. It is then filed as `Artist/Album/` like everything else.
  Albums and songs you already have are left out, and songs missing from an album you have are added to that album.
  The first look at an artist takes a few seconds (cached for 6 hours; the catalog part answers first, the profile
  part joins in on the next look). Setting: "Fill artist pages ...".
- **Find and add:** the settings page can search and add a song directly, for apps that only search a synced copy
  of the library (e.g. Manet) and so never ask the server.
- **Recent download problems:** the settings page lists why downloads failed; clients only show a generic error.

## Install (Docker)

1. `dotnet publish src/Jellyfin.Plugin.YtSearch -c Release`
2. Copy `bin/Release/net9.0/publish/Jellyfin.Plugin.YtSearch.dll` to `/config/plugins/YtSearch_0.1.0.0/` and restart the container.
3. Dashboard > Plugins > My Plugins > YouTube Search > Settings.

No yt-dlp install or extra volume is needed (data lives under `/config/data/ytsearch`).

## Notes

- Files must be m4a: YouTube/SoundCloud tracks without an m4a stream (or with DRM) return a 404 with the reason.
- YouTube may block server IPs ("confirm you're not a bot"); upload cookies if that happens.
- Long SoundCloud mixes take 30-60 s to download on first play.
