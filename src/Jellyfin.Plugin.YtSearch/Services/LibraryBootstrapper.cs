using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Creates the music library shortly after startup, so it exists before the first search or play.</summary>
public class LibraryBootstrapper : BackgroundService
{
    private readonly LibraryService _library;
    private readonly ILogger<LibraryBootstrapper> _logger;

    public LibraryBootstrapper(LibraryService library, ILogger<LibraryBootstrapper> logger)
    {
        _library = library;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let Jellyfin finish starting (and its own first scan) before touching the library.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
            await _library.EnsureLibraryAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prepare the music library at startup");
        }
    }
}
