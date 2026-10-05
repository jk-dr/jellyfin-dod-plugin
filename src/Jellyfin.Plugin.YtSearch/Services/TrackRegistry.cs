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
/// Remembers every search result by id so clients can still play, download or queue it later, including after a
/// restart. The first album decision for a track is kept (its id depends on where the file will live), so later
/// searches never change an id a client already holds. Persisted to disk, pruned by age and size.
/// </summary>
public class TrackRegistry
{
    private const int MaxEntries = 20000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);

    public sealed record Stored(string Source, string SourceId, string Title, string Artist, double Duration, string Thumb, string PageUrl, DateTime LastSeen, TrackMeta? Meta = null, string? Folder = null, string? Group = null);

    public sealed record FileModel(List<Stored>? Tracks, Dictionary<string, bool>? Playable);

    private readonly string _file;
    private readonly ILogger<TrackRegistry> _logger;
    private readonly LibraryService _library;
    private readonly ConcurrentDictionary<Guid, TrackResult> _byId = new();
    private readonly ConcurrentDictionary<string, (TrackResult Track, DateTime LastSeen)> _bySource = new();
    private readonly ConcurrentDictionary<string, bool> _playable = new();
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
        return _byId.TryGetValue(id, out track!);
    }

    /// <summary>All known tracks of one (not yet existing) album.</summary>
    public IReadOnlyList<TrackResult> TracksOfAlbum(Guid albumId)
    {
        EnsureLoaded();
        return _bySource.Values.Select(v => v.Track).Where(t => t.Meta is not null && !t.IsAlbumStub && t.AlbumId == albumId).OrderBy(t => t.Meta!.DiscNumber ?? 1).ThenBy(t => t.Meta!.TrackNumber ?? 999).ThenBy(t => t.DisplayTitle).ToList();
    }

    /// <summary>Everything handed out since <paramref name="since"/>, most recent first.</summary>
    public IReadOnlyList<(TrackResult Track, DateTime LastSeen)> Recent(DateTime since, int max)
    {
        EnsureLoaded();
        return _bySource.Values.Where(v => v.LastSeen >= since).OrderByDescending(v => v.LastSeen).Take(max).Select(v => (v.Track, v.LastSeen)).ToList();
    }

    /// <summary>Finds the track (and the artist's name) behind an artist id handed out in search results.</summary>
    public (TrackResult Track, string Name)? FindArtist(Guid artistId)
    {
        EnsureLoaded();
        foreach (var track in _bySource.Values.Select(v => v.Track))
        {
            var name = track.ArtistNameFor(artistId);
            if (name is not null)
            {
                return (track, name);
            }
        }

        return null;
    }

    /// <summary>The track as first registered, with the ids and album it was given then.</summary>
    public bool TryGetBySource(string source, string sourceId, out TrackResult track)
    {
        EnsureLoaded();
        if (_bySource.TryGetValue($"{source}:{sourceId}", out var e))
        {
            track = e.Track;
            return true;
        }

        track = null!;
        return false;
    }

    /// <summary>Remembered answer to "can this be downloaded?", or null if never checked.</summary>
    public bool? GetPlayable(string source, string sourceId)
    {
        EnsureLoaded();
        return _playable.TryGetValue($"{source}:{sourceId}", out var v) ? v : null;
    }

    public void SetPlayable(string source, string sourceId, bool playable)
    {
        EnsureLoaded();
        _playable[$"{source}:{sourceId}"] = playable;
        SaveSoon();
    }

    public void Add(IEnumerable<TrackResult> tracks)
    {
        EnsureLoaded();
        var now = DateTime.UtcNow;
        foreach (var t in tracks)
        {
            // The search decides which version of a track to hand out (the remembered one, or a recomputed one): store that.
            _bySource[t.Key] = (t, now);
            _byId[t.TrackId] = t;
            _byId[t.AlbumId] = t;
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
                    var text = File.ReadAllText(_file);
                    var model = text.TrimStart().StartsWith('[')
                        ? new FileModel(JsonSerializer.Deserialize<List<Stored>>(text), null) // older format
                        : JsonSerializer.Deserialize<FileModel>(text);
                    foreach (var s in model?.Tracks ?? new List<Stored>())
                    {
                        if (!InputGuard.IsValidSourceId(s.Source, s.SourceId))
                        {
                            continue;
                        }

                        var t = _library.WithId(new TrackResult(
                            s.Source,
                            s.SourceId,
                            InputGuard.CleanText(s.Title, 300, s.SourceId),
                            InputGuard.CleanText(s.Artist, 200, "Unknown"),
                            s.Duration,
                            InputGuard.SafeThumbnailUrl(s.Thumb) ?? string.Empty,
                            InputGuard.SafePageUrl(s.PageUrl) ?? string.Empty) { Meta = Sanitize(s.Meta), FolderOverride = SafeFolder(s.Folder), GroupId = s.Group is { Length: > 0 and <= 15 } g && g.All(char.IsDigit) ? g : null });
                        _bySource[t.Key] = (t, s.LastSeen);
                        _byId[t.TrackId] = t;
                        _byId[t.AlbumId] = t;
                    }

                    foreach (var kv in model?.Playable ?? new Dictionary<string, bool>())
                    {
                        _playable[kv.Key] = kv.Value;
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

    /// <summary>A stored folder is only trusted if it is a plain absolute path to an existing directory.</summary>
    private static string? SafeFolder(string? folder) =>
        folder is { Length: > 0 } && Path.IsPathRooted(folder) && !folder.Contains("..", StringComparison.Ordinal) && Directory.Exists(folder) ? folder : null;

    private static TrackMeta? Sanitize(TrackMeta? m) => m is null
        ? null
        : new TrackMeta(
            InputGuard.CleanText(m.Title, 300, "Unknown"),
            InputGuard.CleanText(m.Artist, 200, "Unknown"),
            InputGuard.CleanText(m.Album.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase) && m.Album.Length > 9 ? m.Album[..^9] : m.Album, 200, "Unknown"), // remembered before singles lost their suffix
            InputGuard.CleanText(m.AlbumArtist, 200, "Unknown"),
            m.Year,
            m.TrackNumber,
            m.DiscNumber,
            m.Genre is null ? null : InputGuard.CleanText(m.Genre, 60, "Music"),
            InputGuard.SafeThumbnailUrl(m.ArtworkUrl));

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
            var rows = _bySource.Values
                .Where(v => v.LastSeen >= cutoff)
                .OrderByDescending(v => v.LastSeen)
                .Take(MaxEntries)
                .Select(v => new Stored(v.Track.Source, v.Track.SourceId, v.Track.Title, v.Track.Artist, v.Track.DurationSeconds, v.Track.ThumbnailUrl, v.Track.PageUrl, v.LastSeen, v.Track.Meta, v.Track.FolderOverride, v.Track.GroupId))
                .ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(new FileModel(rows, new Dictionary<string, bool>(_playable))));
            File.Move(_file + ".tmp", _file, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save track registry");
        }
    }
}
