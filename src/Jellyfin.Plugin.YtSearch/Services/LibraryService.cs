using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Owns the on-disk library folder. A downloaded, tagged file is placed in an Artist/Album folder (or loose in the
/// root when no album is known) and indexed by Jellyfin's own scanner, so albums, artists, artwork and "recently added"
/// behave like any other music. A track's id is the id Jellyfin derives from the file path, so it is known up front.
/// </summary>
public class LibraryService
{
    private const string LibraryName = "YouTube & SoundCloud";

    private static readonly Regex OurFileName = new("^(yt|sc)-[A-Za-z0-9_-]{1,20}\\.m4a$", RegexOptions.Compiled);

    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<LibraryService> _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public LibraryService(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, IApplicationPaths paths, ILogger<LibraryService> logger)
    {
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _paths = paths;
        _logger = logger;
    }

    public string Root =>
        Plugin.Instance?.Configuration.LibraryPath is { Length: > 0 } p ? p : Path.Combine(_paths.DataPath, "ytsearch", "library");

    /// <summary>Where the file for this track lives: Root/AlbumArtist/Album/yt-ID.m4a, or Root/yt-ID.m4a without an album.</summary>
    public string PathFor(TrackResult r)
    {
        // Ids become file names: refuse anything that is not a plain service id (no path separators, no "..").
        if (!InputGuard.IsValidSourceId(r.Source, r.SourceId))
        {
            throw new ArgumentException("Invalid track id", nameof(r));
        }

        var root = Root.TrimEnd('/');
        var name = (r.Source == Sources.SoundCloud ? "sc-" : "yt-") + r.SourceId + ".m4a";
        var path = r.Meta is { } m
            ? Path.Combine(root, InputGuard.SafeFolderName(m.AlbumArtist, 80), InputGuard.SafeFolderName(m.Album, 100), name)
            : Path.Combine(root, name);

        // Whatever the metadata said, the file must end up inside the library folder.
        if (!Path.GetFullPath(path).StartsWith(root + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path escapes the library folder", nameof(r));
        }

        return path;
    }

    /// <summary>Sets the Jellyfin ids this track (and its album) will have once the file is indexed.</summary>
    public TrackResult WithId(TrackResult r)
    {
        var path = PathFor(r);
        var withId = r with { TrackId = _library.GetNewItemId(path, typeof(Audio)) };
        return r.Meta is null
            ? withId
            : withId with { AlbumId = _library.GetNewItemId(Path.GetDirectoryName(path)!, typeof(MusicAlbum)) };
    }

    /// <summary>True for files this plugin created (and so may delete): yt-*.m4a / sc-*.m4a inside the library folder.</summary>
    public bool IsOurFile(string? path) => IsOurFile(Root, path);

    internal static bool IsOurFile(string root, string? path)
    {
        if (string.IsNullOrEmpty(path) || !OurFileName.IsMatch(Path.GetFileName(path)))
        {
            return false;
        }

        var normalizedRoot = root.TrimEnd('/') + "/";
        var dir = Path.GetDirectoryName(path)!.TrimEnd('/') + "/";
        // Directly in the root, or Artist/Album below it. Nothing deeper, nothing outside.
        if (!dir.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            return false;
        }

        var depth = dir[normalizedRoot.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        return depth <= 2 && !path.Contains("/../", StringComparison.Ordinal);
    }

    public long LibrarySizeBytes() =>
        Directory.Exists(Root) ? new DirectoryInfo(Root).EnumerateFiles("*.m4a", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    public BaseItem? GetItem(Guid id) => _library.GetItemById(id);

    /// <summary>True when the item exists in the library and its file is on disk.</summary>
    public bool IsPromoted(Guid id) => GetItem(id) is { Path: { Length: > 0 } p } && File.Exists(p);

    /// <summary>Moves the tagged file into place and has Jellyfin index it.</summary>
    public async Task PromoteAsync(TrackResult r, string taggedFile, CancellationToken ct)
    {
        var path = PathFor(r);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Move(taggedFile, path, true);

        await IndexAsync(ct).ConfigureAwait(false);

        // A scan that was already running (first track, or a library scan in progress) may still be working: wait for the item.
        var id = _library.GetNewItemId(path, typeof(Audio));
        BaseItem? item = null;
        for (var attempt = 0; attempt < 60 && item is null; attempt++)
        {
            item = _library.GetItemById(id);
            if (item is null)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                if (attempt is 6 or 20)
                {
                    await IndexAsync(ct).ConfigureAwait(false); // ask again, now that the library folder exists
                }
            }
        }

        if (item is null)
        {
            throw new InvalidOperationException($"Jellyfin did not index {Path.GetFileName(path)}");
        }

        await RefreshAsync(item, ct).ConfigureAwait(false);
        _logger.LogInformation("Added {Source} track '{Title}' to the library as {Id}{Album}", r.Source, r.DisplayTitle, item.Id, r.Meta is { } m ? $" (album '{m.Album}')" : string.Empty);
    }

    /// <summary>
    /// Reads the tags and embedded cover art now (Jellyfin would do it a little later from its own queue), for the track and
    /// the album and artist above it, so everything is complete by the time the client asks for it.
    /// </summary>
    private async Task RefreshAsync(BaseItem item, CancellationToken ct)
    {
        try
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ReplaceAllImages = true,
            };
            await _providers.RefreshSingleItem(item, options, ct).ConfigureAwait(false);
            for (var parent = item.GetParent(); parent is MusicAlbum or MusicArtist; parent = parent.GetParent())
            {
                await _providers.RefreshSingleItem(parent, options, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Refreshing metadata of {Name} failed: {Message}", item.Name, ex.Message);
        }
    }

    /// <summary>Asks Jellyfin to look at the library folder now (only this folder, not every library).</summary>
    public async Task IndexAsync(CancellationToken ct)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Root);
            if (_library.FindByPath(Root.TrimEnd('/'), true) is Folder folder)
            {
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
                await folder.ValidateChildren(new Progress<double>(), options, true, false, ct).ConfigureAwait(false);
                return;
            }

            // First time: create the library (a global scan once, the library is otherwise empty).
            var normalized = Root.TrimEnd('/');
            if (!_library.GetVirtualFolders().Any(f => f.Locations.Any(l => string.Equals(l.TrimEnd('/'), normalized, StringComparison.Ordinal))))
            {
                _logger.LogInformation("Creating Jellyfin music library '{Name}' at {Root}", LibraryName, Root);
                await _library.AddVirtualFolder(LibraryName, CollectionTypeOptions.music, NewLibraryOptions(Root), false).ConfigureAwait(false);
            }

            await _library.ValidateMediaLibrary(new Progress<double>(), ct).ConfigureAwait(false);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>Removes empty album/artist folders left behind after deletions and lets Jellyfin drop their items.</summary>
    public async Task PruneEmptyFoldersAsync(CancellationToken ct)
    {
        var root = Root.TrimEnd('/');
        if (!Directory.Exists(root))
        {
            return;
        }

        var removed = false;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                removed = true;
            }
        }

        if (removed)
        {
            await IndexAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the item and its file.</summary>
    public void Delete(BaseItem item) =>
        _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });

    /// <summary>No internet lookups or sidecar files: the tags and cover art we wrote are the truth, and nothing is sent anywhere.</summary>
    private static LibraryOptions NewLibraryOptions(string root) => new()
    {
        PathInfos = new[] { new MediaPathInfo(root) },
        EnableInternetProviders = false,
        SaveLocalMetadata = false,
        EnableRealtimeMonitor = false,
        AutomaticRefreshIntervalDays = 0,
        MetadataSavers = Array.Empty<string>(),
    };
}
