using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Downloads a track and turns it into a real library item the first time it is needed.
/// One download per track no matter how many requests ask; a client that disconnects does not cancel it.
/// </summary>
public class DownloadService
{
    private readonly YtDlpService _ytdlp;
    private readonly LibraryService _library;
    private readonly CleanupService _cleanup;
    private readonly AudioTagger _tagger;
    private readonly SearchService _search;
    private readonly LyricsClient _lyrics;
    private readonly FailureLog _failures;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<DownloadService> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    public DownloadService(YtDlpService ytdlp, LibraryService library, CleanupService cleanup, AudioTagger tagger, SearchService search, LyricsClient lyrics, FailureLog failures, IApplicationPaths paths, ILogger<DownloadService> logger)
    {
        _ytdlp = ytdlp;
        _library = library;
        _cleanup = cleanup;
        _tagger = tagger;
        _search = search;
        _lyrics = lyrics;
        _failures = failures;
        _paths = paths;
        _logger = logger;
    }

    public bool IsDownloading(Guid id) => _inFlight.ContainsKey(id);

    /// <summary>Completes when the track exists in the library. Throws <see cref="DownloadException"/> on failure.</summary>
    public Task EnsureAsync(TrackResult track, CancellationToken requestAborted)
    {
        if (_library.IsPromoted(track.TrackId))
        {
            return Task.CompletedTask;
        }

        var task = _inFlight.GetOrAdd(track.TrackId, _ => Task.Run(() => RunAsync(track)));
        return task.WaitAsync(requestAborted);
    }

    /// <summary>Refuses new downloads once the library is over its size limit (after trying a cleanup first).</summary>
    private async Task EnsureSpaceAsync(TrackResult track)
    {
        var limit = (long)Math.Max(Plugin.Instance?.Configuration.MaxLibraryMegabytes ?? 20480, 0) * 1024 * 1024;
        if (limit == 0 || _library.LibrarySizeBytes() <= limit)
        {
            return;
        }

        await _cleanup.RunAsync(track.TrackId).ConfigureAwait(false);
        if (_library.LibrarySizeBytes() > limit)
        {
            throw new DownloadException("The download library is full. Free some space or raise the limit in the plugin settings.");
        }
    }

    /// <summary>
    /// Downloads the song. A catalog song has no source yet: the best matching YouTube upload is tried first, then SoundCloud
    /// ones, moving on to the next copy when one cannot be downloaded.
    /// </summary>
    private async Task<(string File, TrackResult Origin)> DownloadAsync(TrackResult track, string tmp, CancellationToken ct)
    {
        if (track.Source != Sources.Catalog)
        {
            return (await _ytdlp.DownloadAsync(track, tmp, ct).ConfigureAwait(false), track);
        }

        var candidates = await _search.FindSourcesAsync(track, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new DownloadException("No YouTube or SoundCloud copy of this song was found.");
        }

        DownloadException? last = null;
        // The best few YouTube copies, then a few SoundCloud ones (so a blocked or failing YouTube never hides SoundCloud).
        var attempts = candidates.Where(c => c.Source == Sources.YouTube).Take(3).Concat(candidates.Where(c => c.Source != Sources.YouTube).Take(3));
        foreach (var candidate in attempts)
        {
            try
            {
                return (await _ytdlp.DownloadAsync(candidate, tmp, ct).ConfigureAwait(false), candidate);
            }
            catch (DownloadException ex)
            {
                last = ex;
                _logger.LogInformation("Copy {Source} {Id} of '{Title}' failed ({Message}); trying the next", candidate.Source, candidate.SourceId, track.DisplayTitle, ex.Message);
            }
        }

        throw last ?? new DownloadException("This song could not be downloaded.");
    }

    private async Task RunAsync(TrackResult track)
    {
        var tmp = Path.Combine(_paths.DataPath, "ytsearch", "tmp", Guid.NewGuid().ToString("N"));
        try
        {
            if (_library.IsPromoted(track.TrackId))
            {
                return;
            }

            if (track.IsAlbumStub || (track.Source != Sources.Catalog && string.IsNullOrEmpty(track.PageUrl)))
            {
                throw new DownloadException("This track is unavailable.");
            }

            await EnsureSpaceAsync(track).ConfigureAwait(false);

            var timeout = TimeSpan.FromSeconds(Math.Max(Plugin.Instance?.Configuration.DownloadTimeoutSeconds ?? 300, 10));
            using var cts = new CancellationTokenSource(timeout);
            _logger.LogInformation("Downloading {Source} '{Title}' ({Id})", track.Source, track.Title, track.SourceId);
            var lyricsTask = _lyrics.FetchAsync(track, cts.Token); // runs while the audio downloads
            var (file, origin) = await DownloadAsync(track, tmp, cts.Token).ConfigureAwait(false);
            var (tagged, gain) = await _tagger.TagAsync(file, track, tmp, cts.Token, origin).ConfigureAwait(false);
            var sidecar = LyricsClient.ForSidecar(await lyricsTask.ConfigureAwait(false));
            await _library.PromoteAsync(track, tagged, CancellationToken.None, sidecar, gain).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _failures.Add(track, "The download timed out.");
            throw new DownloadException("The download timed out.");
        }
        catch (DownloadException ex)
        {
            _logger.LogWarning("Download of {Source} {Id} failed: {Message}", track.Source, track.SourceId, ex.Message);
            _failures.Add(track, ex.Message);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(track.TrackId, out _);
            try { if (Directory.Exists(tmp)) { Directory.Delete(tmp, true); } } catch (IOException) { }
        }

        // A new song was just added: tidy up old ones (not the one just added).
        _cleanup.RunInBackground(track.TrackId);
    }
}
