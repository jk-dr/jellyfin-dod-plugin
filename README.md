# Jellyfin YouTube + SoundCloud search plugin (personal use)

Appends YouTube and SoundCloud results to Jellyfin's standard search API (`/Items?searchTerm=`,
`/Search/Hints`), so any Jellyfin client shows them as normal songs. Target: Jellyfin 10.11.11 (.NET 9).

## How it works

- **Search:** `yt-dlp ytsearchN:` and `scsearchN:` run in parallel with Jellyfin's own search; results are
  appended to the JSON and cached for a few minutes. Result ids are the ids Jellyfin would give the file.
- **First use:** when a client plays, downloads, favorites or adds a result to a playlist, the plugin downloads
  the m4a (no conversion), adds it to an auto-created "YouTube & SoundCloud" music library and then lets
  the original request through, so streaming, ranges, transcoding and favorites are Jellyfin's own.
- **Cleanup:** after each new download (and daily at 04:00) tracks played at most 3 times, not a favorite of any
  user, in no playlist and untouched for 7 days are deleted. Their ids keep working (they download again on demand).
- **yt-dlp:** the plugin downloads its own nightly binary into the data folder and updates it every 12 hours.
- **Cookies:** upload a Netscape `cookies.txt` on the plugin settings page (age-restricted / bot-check videos);
  the Refresh button checks that they still work. Cookies are only sent to YouTube.

## Install (Docker)

1. `dotnet publish src/Jellyfin.Plugin.YtSearch -c Release`
2. Copy `bin/Release/net9.0/publish/Jellyfin.Plugin.YtSearch.dll` to `/config/plugins/YtSearch_0.1.0.0/` and restart the container.
3. Dashboard > Plugins > My Plugins > YouTube Search > Settings.

No yt-dlp install or extra volume is needed (data lives under `/config/data/ytsearch`).

## Notes

- Files must be m4a: YouTube/SoundCloud tracks without an m4a stream (or with DRM) return a 404 with the reason.
- YouTube may block server IPs ("confirm you're not a bot"); upload cookies if that happens.
- Long SoundCloud mixes take 30-60 s to download on first play.
