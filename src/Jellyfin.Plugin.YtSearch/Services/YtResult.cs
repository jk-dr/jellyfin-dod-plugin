using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.YtSearch.Services;

public sealed record YtResult(string VideoId, string Title, string Channel, double DurationSeconds, string ThumbnailUrl)
{
    public long RunTimeTicks => (long)(DurationSeconds * TimeSpan.TicksPerSecond);

    public Guid TrackId => StableGuid("yt-track:" + VideoId);

    public Guid AlbumId => StableGuid("yt-album:" + VideoId);

    public Guid ArtistId => StableGuid("yt-artist:" + Channel);

    private static Guid StableGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
