# Jellyfin YouTube + SoundCloud search plugin (personal use)

Appends YouTube and SoundCloud results to Jellyfin's standard search API (`/Items?searchTerm=`,
`/Search/Hints`), so any Jellyfin client shows them as normal songs. Target: Jellyfin 10.11.11 (.NET 9).

## How it works

- **Search:** YouTube and SoundCloud are queried directly over HTTP (about 0.5 s; yt-dlp is the automatic fallback),
  merged and ranked by relevance to what you typed, and appended to the JSON of `/Items?searchTerm=` and
  `/Search/Hints`. Result ids are the ids Jellyfin will give the files, so they never change.
- **Volume levelling:** each download is measured with ffmpeg (about 2-3 s for a 4-minute song) and the result is
  stored as ReplayGain tags (`REPLAYGAIN_TRACK_GAIN`, reference -18 LUFS). The audio is never re-encoded. Players
  that apply ReplayGain or Jellyfin's normalisation level the songs. Setting: "Level the volume of downloads".
- **Timed lyrics:** while the audio downloads, the song is looked up on lrclib.net (artist, title, album and length
  are sent there; 6 s limit, failures are ignored). Timed lyrics are saved as `yt-ID.lrc` (or `.txt` when only
  plain lyrics exist) next to the song, where Jellyfin reads them. Cleanup deletes them with the song. Setting:
  "Fetch timed lyrics".
- **Real albums:** each result is matched against Apple's iTunes Search (no key) for its real album, track number,
  year and cover. Only confident matches get an album (no remixes, covers or live versions); the rest are loose tracks.
- **First use:** when a client plays, downloads, favorites or adds a result to a playlist, the plugin downloads the
  m4a, writes the tags and cover into it with ffmpeg stream copy (no re-encoding), files it as
  `Artist/Album/yt-ID.m4a` (or loose in the root), and has Jellyfin's own scanner index it. Albums, artists, artwork
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
  one you already downloaded, otherwise the YouTube one. Copies on the same site are left alone.
- **Artists:** several artists in a credit ("A, B & C") each get their own artist; " - Topic" is dropped from channel
  names. Opening an artist adds that artist's songs from the sites; artists of search results have a page too.
- **Choosing the album:** by default a song goes into its real album (or stays loose). In the settings you can
  instead send every download into one album you name. On the "Find and add" box you can pick, per song, an album
  you already have (the file is saved into that album's folder, so Jellyfin needs write access there) or a new name.
  Songs placed into an album you picked are never auto-deleted.
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
