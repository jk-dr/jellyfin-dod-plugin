using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>Deletes downloaded tracks nobody is using any more. Uses Jellyfin's own play counts, favorites and playlists.</summary>
public class CleanupService
{
    private readonly ILibraryManager _library;
    private readonly LibraryService _libraryService;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IPlaylistManager _playlists;
    private readonly ISessionManager _sessions;
    private readonly ILogger<CleanupService> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public CleanupService(
        ILibraryManager library,
        LibraryService libraryService,
        IUserManager users,
        IUserDataManager userData,
        IPlaylistManager playlists,
        ISessionManager sessions,
        ILogger<CleanupService> logger)
    {
        _library = library;
        _libraryService = libraryService;
        _users = users;
        _userData = userData;
        _playlists = playlists;
        _sessions = sessions;
        _logger = logger;
    }

    public void RunInBackground(Guid? exclude = null) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(exclude).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup failed");
            }
        });

    /// <summary>Returns the number of tracks deleted.</summary>
    public async Task<int> RunAsync(Guid? exclude = null)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !cfg.EnableCleanup)
        {
            return 0;
        }

        await _running.WaitAsync().ConfigureAwait(false);
        try
        {
            var root = _libraryService.Root.TrimEnd('/') + "/";
            var tracks = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Audio }, Recursive = true })
                .Where(i => i.Path != null && i.Path.StartsWith(root, StringComparison.Ordinal))
                .ToList();
            if (tracks.Count == 0)
            {
                return 0;
            }

            var users = _users.GetUsers().ToList();
            var inPlaylist = new HashSet<Guid>();
            foreach (var user in users)
            {
                foreach (var playlist in _playlists.GetPlaylists(user.Id))
                {
                    foreach (var child in playlist.LinkedChildren)
                    {
                        if (child.ItemId is { } id)
                        {
                            inPlaylist.Add(id);
                        }
                    }
                }
            }

            var playing = _sessions.Sessions.Select(s => s.NowPlayingItem?.Id).Where(i => i.HasValue).Select(i => i!.Value).ToHashSet();
            var now = DateTime.UtcNow;
            var deleted = 0;
            foreach (var track in tracks)
            {
                if (track.Id == exclude || playing.Contains(track.Id))
                {
                    continue;
                }

                var plays = 0;
                var favorite = false;
                var lastActivity = track.DateCreated;
                foreach (var user in users)
                {
                    var data = _userData.GetUserData(user, track);
                    if (data is null)
                    {
                        continue;
                    }

                    plays += data.PlayCount;
                    favorite |= data.IsFavorite;
                    if (data.LastPlayedDate is { } played && played > lastActivity)
                    {
                        lastActivity = played;
                    }
                }

                if (CleanupPolicy.ShouldDelete(plays, favorite, inPlaylist.Contains(track.Id), lastActivity, now, cfg.CleanupMaxPlays, cfg.CleanupRetentionDays))
                {
                    _logger.LogInformation("Cleanup: deleting '{Name}' (plays {Plays}, last activity {Last:u})", track.Name, plays, lastActivity);
                    _libraryService.Delete(track);
                    deleted++;
                }
            }

            return deleted;
        }
        finally
        {
            _running.Release();
        }
    }
}
