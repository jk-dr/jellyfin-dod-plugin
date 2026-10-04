using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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

    // Folders that can't be the library itself ...
    private static readonly string[] ForbiddenExactly = { "/", "/home", "/opt", "/tmp", "/var" };

    // ... and system trees nothing may be written into.
    private static readonly string[] ForbiddenTrees = { "/etc", "/bin", "/sbin", "/usr", "/lib", "/lib32", "/lib64", "/proc", "/sys", "/dev", "/boot", "/root", "/run" };

    /// <summary>Name of the library older versions created on their own. It is never created now, and only chosen automatically as a last resort.</summary>
    private const string LegacyLibraryName = "YouTube & SoundCloud";

    private readonly object _autoLock = new();
    private (DateTime At, string? Path) _auto;

    /// <summary>
    /// The folder downloads go into: the one chosen in the settings, else the first writable music library Jellyfin already
    /// has. The plugin never creates a library; null when there is nothing to use.
    /// </summary>
    public string? TryRoot
    {
        get
        {
            var configured = Plugin.Instance?.Configuration.LibraryPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (IsAcceptableRoot(configured))
                {
                    return configured.Trim().TrimEnd('/');
                }

                if (!_warnedAboutRoot)
                {
                    _warnedAboutRoot = true;
                    _logger.LogWarning("The library folder '{Path}' in the settings is not usable (must be an absolute, non-system folder); using an existing music library instead", configured);
                }
            }

            return AutoRoot();
        }
    }

    /// <summary>Same, but throws a <see cref="DownloadException"/> that tells the user what to do when there is no library.</summary>
    public string Root => TryRoot ?? throw new DownloadException("There is no music library to put downloads in. Add a music library in Jellyfin, or choose one in the plugin settings.");

    /// <summary>The first writable music library (other libraries before the one old versions created), looked up at most once a minute.</summary>
    private string? AutoRoot()
    {
        lock (_autoLock)
        {
            if (DateTime.UtcNow - _auto.At < TimeSpan.FromMinutes(1))
            {
                return _auto.Path;
            }

            var path = MusicLibraries().Where(l => l.Writable).OrderBy(l => l.Name == LegacyLibraryName).Select(l => l.Path).FirstOrDefault();
            _auto = (DateTime.UtcNow, path);
            return path;
        }
    }

    private bool _warnedAboutRoot;

    /// <summary>An absolute path that is not "/" or a system directory (or inside one) and has no ".." in it.</summary>
    internal static bool IsAcceptableRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim().TrimEnd('/');
        if (!trimmed.StartsWith('/') || trimmed.Split('/').Contains("..") || trimmed.Any(char.IsControl))
        {
            return false;
        }

        return !ForbiddenExactly.Contains(trimmed, StringComparer.Ordinal)
            && !ForbiddenTrees.Any(t => trimmed.Equals(t, StringComparison.Ordinal) || trimmed.StartsWith(t + "/", StringComparison.Ordinal));
    }

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
        // Artist and album folders are created when the song is added: Root/Artist/Album/yt-ID.m4a.
        var path = r.Meta is { } m
            ? Path.Combine(root, InputGuard.SafeFolderName(r.PrimaryAlbumArtist, 80), InputGuard.SafeFolderName(m.Album, 100), name)
            : Path.Combine(root, InputGuard.SafeFolderName(r.CleanArtist, 80), InputGuard.SafeFolderName(r.Title, 100), name);

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
    public bool IsOurFile(string? path) => TryRoot is { } root && IsOurFile(root, path);

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

    /// <summary>Space used by the files this plugin downloaded (not the rest of a library the user may have pointed it at).</summary>
    public long LibrarySizeBytes()
    {
        var root = TryRoot;
        return root is not null && Directory.Exists(root)
            ? new DirectoryInfo(root).EnumerateFiles("*.m4a", SearchOption.AllDirectories).Where(f => IsOurFile(root, f.FullName)).Sum(f => f.Length)
            : 0;
    }

    public BaseItem? GetItem(Guid id) => _library.GetItemById(id);

    /// <summary>True when the item exists in the library and its file is on disk.</summary>
    public bool IsPromoted(Guid id) => GetItem(id) is { Path: { Length: > 0 } p } && File.Exists(p);

    /// <summary>Moves the tagged file into place and has Jellyfin index it.</summary>
    public async Task PromoteAsync(TrackResult r, string taggedFile, CancellationToken ct, (string Extension, string Content)? lyrics = null)
    {
        var path = PathFor(r);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(taggedFile, path, true);
            if (lyrics is { } l)
            {
                // Jellyfin reads lyrics from a file with the song's name; it must be there before the scan.
                File.WriteAllText(Path.ChangeExtension(path, l.Extension), l.Content);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning("Could not write to {Folder}: {Message}", Path.GetDirectoryName(path), ex.Message);
            throw new DownloadException("Jellyfin can't write to the library folder (read-only mount or permissions?). Check the Library setting.");
        }

        await IndexAsync(Path.GetDirectoryName(path)!, ct).ConfigureAwait(false);

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
                    await IndexAsync(Path.GetDirectoryName(path)!, ct).ConfigureAwait(false); // ask again, now that the library folder exists
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

            // The album and artist above it may already exist (even be the user's own): fill in what is missing, never overwrite.
            var fillOnly = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
            };
            for (var parent = item.GetParent(); parent is MusicAlbum or MusicArtist; parent = parent.GetParent())
            {
                await _providers.RefreshSingleItem(parent, fillOnly, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Refreshing metadata of {Name} failed: {Message}", item.Name, ex.Message);
        }
    }

    /// <summary>
    /// Asks Jellyfin to look at <paramref name="folder"/> now: the nearest folder Jellyfin already knows (the album, the artist,
    /// or the library root), so only a small part of the library is scanned.
    /// </summary>
    public async Task IndexAsync(string folder, CancellationToken ct, bool recursive = true)
    {
        await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var dir = folder.TrimEnd('/'); dir.Length > 1; dir = Path.GetDirectoryName(dir) ?? string.Empty)
            {
                if (_library.FindByPath(dir, true) is Folder known)
                {
                    var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
                    await known.ValidateChildren(new Progress<double>(), options, recursive, false, ct).ConfigureAwait(false);
                    return;
                }
            }

            // Nothing known above the folder: the library is new or has never been scanned. Let Jellyfin scan its libraries once.
            await _library.ValidateMediaLibrary(new Progress<double>(), ct).ConfigureAwait(false);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>The Jellyfin library whose folder is (or contains) <paramref name="folder"/>.</summary>
    private VirtualFolderInfo? LibraryContaining(string folder)
    {
        var f = folder.TrimEnd('/');
        return _library.GetVirtualFolders().FirstOrDefault(v => v.Locations.Any(l =>
        {
            var loc = l.TrimEnd('/');
            return f.Equals(loc, StringComparison.Ordinal) || f.StartsWith(loc + "/", StringComparison.Ordinal);
        }));
    }

    /// <summary>
    /// Online results are only added for users who may use the library downloads go to: administrators, users with access
    /// to all libraries, and users with this library enabled. Disabled users never.
    /// </summary>
    public bool UserCanUse(User user) => TryRoot is not null && Allows(
        user.HasPermission(PermissionKind.IsAdministrator),
        user.HasPermission(PermissionKind.IsDisabled),
        user.HasPermission(PermissionKind.EnableAllFolders),
        user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders),
        OurLibraryId());

    internal static bool Allows(bool isAdministrator, bool isDisabled, bool allFolders, IEnumerable<Guid> enabledFolders, Guid? libraryId)
    {
        if (isDisabled)
        {
            return false;
        }

        if (isAdministrator || allFolders)
        {
            return true;
        }

        return libraryId is { } id && enabledFolders.Contains(id);
    }

    private Guid? OurLibraryId() =>
        TryRoot is { } root && LibraryContaining(root) is { } lib && Guid.TryParse(lib.ItemId, out var id) ? id : null;

    /// <summary>The name of an artist Jellyfin already knows.</summary>
    public string? ArtistName(Guid id) => _library.GetItemById(id) is MusicArtist artist ? artist.Name : null;

    /// <summary>Music libraries (and their folders) the downloads can go into, plus the one in use now.</summary>
    public (string? Path, bool Exists, bool Writable, string? LibraryName, bool Automatic) CurrentLibrary()
    {
        var root = TryRoot;
        if (root is null)
        {
            return (null, false, false, null, true);
        }

        var exists = Directory.Exists(root);
        return (root, exists, exists && CanWrite(root), LibraryContaining(root)?.Name, string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.LibraryPath));
    }

    public IReadOnlyList<(string Name, string Path, bool Writable)> MusicLibraries() =>
        _library.GetVirtualFolders()
            .Where(v => v.CollectionType is null or CollectionTypeOptions.music)
            .SelectMany(v => v.Locations.Select(l => (v.Name, Path: l.TrimEnd('/'))))
            .Where(l => IsAcceptableRoot(l.Path))
            .Select(l => (l.Name, l.Path, Directory.Exists(l.Path) && CanWrite(l.Path)))
            .ToList();

    internal static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the album/artist folders that deleted tracks left empty (and only those: never other empty folders, never the
    /// library root) and lets Jellyfin drop their items.
    /// </summary>
    public async Task PruneEmptyFoldersAsync(IEnumerable<string> folders, CancellationToken ct)
    {
        if (TryRoot is not { } rootPath)
        {
            return;
        }

        var root = rootPath.TrimEnd('/');
        string? changedAbove = null;
        foreach (var start in folders.Distinct())
        {
            var dir = start.TrimEnd('/');
            while (dir.StartsWith(root + "/", StringComparison.Ordinal) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir)!.TrimEnd('/');
                changedAbove = dir;
            }
        }

        if (changedAbove is not null)
        {
            // Just the nearest remaining folder, one level: enough for Jellyfin to drop the missing album/artist, cheap on a big library.
            await IndexAsync(changedAbove, ct, recursive: false).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the item and its file.</summary>
    public void Delete(BaseItem item)
    {
        var path = item.Path;
        _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
        if (IsOurFile(path))
        {
            foreach (var ext in new[] { ".lrc", ".txt" })
            {
                try { File.Delete(Path.ChangeExtension(path, ext)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
