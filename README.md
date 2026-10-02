# Jellyfin YouTube + SoundCloud search plugin (personal use)

Appends YouTube and SoundCloud results to Jellyfin's standard search API (`/Items?searchTerm=`,
`/Search/Hints`), so any Jellyfin client shows them as normal songs. Target: Jellyfin 10.11.11 (.NET 9).

## How it works

- **Search:** YouTube and SoundCloud are queried directly over HTTP (about 0.5 s; yt-dlp is the automatic fallback),
  merged and ranked by relevance to what you typed, and appended to the JSON of `/Items?searchTerm=` and
  `/Search/Hints`. Result ids are the ids Jellyfin will give the files, so they never change.
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
