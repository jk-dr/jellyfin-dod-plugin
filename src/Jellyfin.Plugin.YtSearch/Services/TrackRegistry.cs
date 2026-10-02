using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Remembers every search result by id so clients can still play, download or queue it later,
/// including after a server restart. Persisted to disk, pruned by age and size.
/// </summary>
public class TrackRegistry
{
    private const int MaxEntries = 20000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);

    private sealed record Stored(string Source, string SourceId, string Title, string Artist, double Duration, string Thumb, string PageUrl, DateTime LastSeen, bool? Playable = null);

    private readonly string _file;
    private readonly ILogger<TrackRegistry> _logger;
    private readonly LibraryService _library;
    private readonly ConcurrentDictionary<Guid, (TrackResult Track, DateTime LastSeen)> _byId = new();
    private readonly ConcurrentDictionary<Guid, bool> _playable = new();
    private readonly object _saveLock = new();
    private bool _loaded;
    private bool _saveScheduled;

    public TrackRegistry(IApplicationPaths paths, LibraryService library, ILogger<TrackRegistry> logger)
    {
        _file = Path.Combine(paths.DataPath, "ytsearch", "registry.json");
        _library = library;
        _logger = logger;
    }

    public bool TryGet(Guid id, out TrackResult track)
    {
        EnsureLoaded();
        if (_byId.TryGetValue(id, out var e))
        {
            track = e.Track;
            return true;
        }

        track = null!;
        return false;
    }

    /// <summary>Remembered answer to "can this be downloaded?", or null if never checked.</summary>
    public bool? GetPlayable(Guid trackId)
    {
        EnsureLoaded();
        return _playable.TryGetValue(trackId, out var v) ? v : null;
    }

    public void SetPlayable(Guid trackId, bool playable)
    {
        EnsureLoaded();
        _playable[trackId] = playable;
        SaveSoon();
    }

    public void Add(IEnumerable<TrackResult> tracks)
    {
        EnsureLoaded();
        var now = DateTime.UtcNow;
        foreach (var t in tracks)
        {
            _byId[t.TrackId] = (t, now);
            _byId[t.AlbumId] = (t, now);
        }

        SaveSoon();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_saveLock)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (File.Exists(_file))
                {
                    var items = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_file)) ?? new();
                    foreach (var s in items)
                    {
                        if (!InputGuard.IsValidSourceId(s.Source, s.SourceId))
                        {
                            continue;
                        }

                        var t = _library.WithId(new TrackResult(s.Source, s.SourceId, InputGuard.CleanText(s.Title, 300, s.SourceId), InputGuard.CleanText(s.Artist, 200, "Unknown"), s.Duration, InputGuard.SafeThumbnailUrl(s.Thumb) ?? string.Empty, InputGuard.SafePageUrl(s.PageUrl) ?? string.Empty));
                        _byId[t.TrackId] = (t, s.LastSeen);
                        _byId[t.AlbumId] = (t, s.LastSeen);
                        if (s.Playable is { } p)
                        {
                            _playable[t.TrackId] = p;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read track registry; starting empty");
            }

            _loaded = true;
        }
    }

    /// <summary>Saves a few seconds from now; changes arriving in the meantime ride along in that one save.</summary>
    private void SaveSoon()
    {
        lock (_saveLock)
        {
            if (_saveScheduled)
            {
                return;
            }

            _saveScheduled = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            lock (_saveLock)
            {
                _saveScheduled = false;
            }

            Save();
        });
    }

    private void Save()
    {
        try
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            var rows = _byId.Values
                .Where(v => v.LastSeen >= cutoff)
                .GroupBy(v => v.Track.TrackId)
                .Select(g => g.First())
                .OrderByDescending(v => v.LastSeen)
                .Take(MaxEntries)
                .Select(v => new Stored(v.Track.Source, v.Track.SourceId, v.Track.Title, v.Track.Artist, v.Track.DurationSeconds, v.Track.ThumbnailUrl, v.Track.PageUrl, v.LastSeen, _playable.TryGetValue(v.Track.TrackId, out var pl) ? pl : null))
                .ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(rows));
            File.Move(_file + ".tmp", _file, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save track registry");
        }
    }
}
