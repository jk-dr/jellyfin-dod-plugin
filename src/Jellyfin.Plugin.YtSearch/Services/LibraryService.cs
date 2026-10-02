using System;
using System.IO;
using System.Linq;
using System.Net.Http;
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
/// Owns the on-disk library folder and turns downloaded files into real Jellyfin items.
/// A track's id is the id Jellyfin itself derives from the file path, so later library scans
/// find the same item instead of creating a duplicate.
/// </summary>
public class LibraryService
{
    private const string LibraryName = "YouTube & SoundCloud";

    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<LibraryService> _logger;
    private readonly SemaphoreSlim _setupLock = new(1, 1);

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

    public string PathFor(string source, string sourceId) =>
        Path.Combine(Root, (source == Sources.SoundCloud ? "sc-" : "yt-") + sourceId + ".m4a");

    public TrackResult WithId(TrackResult r) =>
        r with { TrackId = _library.GetNewItemId(PathFor(r.Source, r.SourceId), typeof(Audio)) };

    public BaseItem? GetItem(Guid id) => _library.GetItemById(id);

    /// <summary>True when the item exists in the library and its file is on disk.</summary>
    public bool IsPromoted(Guid id) => GetItem(id) is { Path: { Length: > 0 } p } && File.Exists(p);

    /// <summary>
    /// Makes sure the music library exists in Jellyfin and returns the folder item for its directory,
    /// which is the parent of our tracks. Created directly (what a scan would do) so no full scan is needed.
    /// </summary>
    private async Task<BaseItem> EnsureParentAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        var existing = _library.FindByPath(Root, true);
        if (existing is not null)
        {
            return existing;
        }

        await _setupLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            existing = _library.FindByPath(Root, true);
            if (existing is not null)
            {
                return existing;
            }

            var normalized = Root.TrimEnd('/');
            if (!_library.GetVirtualFolders().Any(f => f.Locations.Any(l => string.Equals(l.TrimEnd('/'), normalized, StringComparison.Ordinal))))
            {
                _logger.LogInformation("Creating Jellyfin music library '{Name}' at {Root}", LibraryName, Root);
                var options = new LibraryOptions { PathInfos = new[] { new MediaPathInfo(Root) } };
                await _library.AddVirtualFolder(LibraryName, CollectionTypeOptions.music, options, false).ConfigureAwait(false);
            }

            var now = DateTime.UtcNow;
            var folder = new Folder
            {
                Id = _library.GetNewItemId(Root, typeof(Folder)),
                Path = Root,
                Name = System.IO.Path.GetFileName(normalized),
                ParentId = _library.RootFolder.Id,
                DateCreated = now,
                DateModified = now,
            };
            _library.CreateItem(folder, _library.RootFolder);
            return folder;
        }
        finally
        {
            _setupLock.Release();
        }
    }

    /// <summary>Moves the downloaded file into the library and creates the real item.</summary>
    public async Task PromoteAsync(TrackResult r, string downloadedFile, CancellationToken ct)
    {
        var path = PathFor(r.Source, r.SourceId);
        Directory.CreateDirectory(Root);
        File.Move(downloadedFile, path, true);
        var thumb = await DownloadThumbnailAsync(r, path, ct).ConfigureAwait(false);

        // The file goes in first: Jellyfin ignores empty library folders, and on the very first
        // track the library scan below may already create the item itself.
        var parent = await EnsureParentAsync(ct).ConfigureAwait(false);
        var id = _library.GetNewItemId(path, typeof(Audio));
        var now = DateTime.UtcNow;
        var item = _library.GetItemById(id) as Audio;
        var isNew = item is null;
        item ??= new Audio { Id = id, Path = path, ParentId = parent.Id, DateCreated = now };
        item.ParentId = parent.Id;
        item.Name = r.Title;
        item.Artists = new[] { r.Artist };
        item.AlbumArtists = new[] { r.Artist };
        item.Album = r.Title;
        item.RunTimeTicks = r.RunTimeTicks;
        item.Container = "m4a";
        item.Size = new FileInfo(path).Length;
        item.DateModified = now;
        item.LockedFields = new[] { MetadataField.Name };
        if (thumb is not null)
        {
            item.SetImage(new ItemImageInfo { Path = thumb, Type = ImageType.Primary }, 0);
        }

        if (isNew)
        {
            _library.CreateItem(item, parent);
        }
        else
        {
            await _library.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }

        // Probe the file so media streams (codec, bitrate, real duration) are known for playback.
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
        };
        await _providers.RefreshSingleItem(item, options, ct).ConfigureAwait(false);
        _logger.LogInformation("Added {Source} track '{Title}' to the library as {Id}", r.Source, r.Title, item.Id);
    }

    /// <summary>Deletes the item and its files.</summary>
    public void Delete(BaseItem item)
    {
        var path = item.Path;
        _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
        if (!string.IsNullOrEmpty(path))
        {
            var thumb = Path.ChangeExtension(path, ".jpg");
            if (File.Exists(thumb))
            {
                File.Delete(thumb);
            }
        }
    }

    private async Task<string?> DownloadThumbnailAsync(TrackResult r, string audioPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.ThumbnailUrl))
        {
            return null;
        }

        try
        {
            var target = Path.ChangeExtension(audioPath, ".jpg");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var bytes = await http.GetByteArrayAsync(r.ThumbnailUrl, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogWarning("Thumbnail download failed for {Id}: {Message}", r.SourceId, ex.Message);
            return null;
        }
    }
}
