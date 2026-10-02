using System;
using System.Collections.Concurrent;
using System.IO;
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
    private readonly IApplicationPaths _paths;
    private readonly ILogger<DownloadService> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    public DownloadService(YtDlpService ytdlp, LibraryService library, CleanupService cleanup, IApplicationPaths paths, ILogger<DownloadService> logger)
    {
        _ytdlp = ytdlp;
        _library = library;
        _cleanup = cleanup;
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

    private async Task RunAsync(TrackResult track)
    {
        var tmp = Path.Combine(_paths.DataPath, "ytsearch", "tmp", Guid.NewGuid().ToString("N"));
        try
        {
            if (_library.IsPromoted(track.TrackId))
            {
                return;
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(Plugin.Instance?.Configuration.DownloadTimeoutSeconds ?? 300, 10));
            using var cts = new CancellationTokenSource(timeout);
            _logger.LogInformation("Downloading {Source} '{Title}' ({Id})", track.Source, track.Title, track.SourceId);
            var file = await _ytdlp.DownloadAsync(track, tmp, cts.Token).ConfigureAwait(false);
            await _library.PromoteAsync(track, file, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new DownloadException("The download timed out.");
        }
        catch (DownloadException ex)
        {
            _logger.LogWarning("Download of {Source} {Id} failed: {Message}", track.Source, track.SourceId, ex.Message);
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
