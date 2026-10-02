# Jellyfin YouTube Search plugin (personal use)

Appends YouTube results to Jellyfin's standard search API as Audio items,
downloaded with yt-dlp on first play. Target: Jellyfin 10.11.11 (.NET 9).

## Phase 0 server setup (headless Linux)

1. `sudo curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && sudo chmod a+rx /usr/local/bin/yt-dlp`
   (also install `ffmpeg`; Jellyfin already ships jellyfin-ffmpeg)
2. `sudo mkdir -p /var/lib/jellyfin/youtube && sudo chown jellyfin: /var/lib/jellyfin/youtube`
3. In Jellyfin: add a Music library pointing at that folder.
4. Build: `dotnet publish src/Jellyfin.Plugin.YtSearch -c Release`, copy the DLL to
   `<jellyfin-data>/plugins/YtSearch_0.1.0.0/` and restart Jellyfin.
