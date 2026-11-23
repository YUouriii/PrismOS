using PrismOS.Gallery;
using PrismOS.Tray;

namespace PrismOS.Tests;

/// <summary>The gallery's "Your files" source points straight at the user's own media. These cover
/// the promises that make that safe: nothing is downloaded for a local entry, nothing deletes the
/// user's file, and the folder lists as stable, previewable entries.</summary>
public class GalleryLibraryTests
{
    private static GalleryEntry LocalEntry(string path) => new()
    {
        Id = WallpaperLibrary.IdFor(path),
        Title = Path.GetFileNameWithoutExtension(path),
        Type = "image",
        Url = path,
        IsLocal = true,
    };

    [Fact]
    public void IdFor_IsStableAndPathSpecific()
    {
        var a = WallpaperLibrary.IdFor(@"C:\walls\one.jpg");
        Assert.Equal(a, WallpaperLibrary.IdFor(@"C:\walls\one.jpg"));
        Assert.Equal(a, WallpaperLibrary.IdFor(@"C:\WALLS\ONE.JPG"));   // case-insensitive on Windows
        Assert.NotEqual(a, WallpaperLibrary.IdFor(@"C:\walls\two.jpg"));
        Assert.StartsWith("lib-", a);
    }

    [Fact]
    public async Task LocalEntry_IsItsOwnDownload_AndCannotBeDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prismos-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "mine.jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            var service = new GalleryService();
            var entry = LocalEntry(file);

            Assert.Equal(file, service.LocalPathIfDownloaded(entry));
            Assert.Equal(file, await service.EnsureDownloadedAsync(entry));
            // "Remove" must never touch a file the user owns.
            Assert.False(service.DeleteDownload(entry));
            Assert.True(File.Exists(file));

            entry.Url = Path.Combine(dir, "gone.jpg");
            Assert.Null(service.LocalPathIfDownloaded(entry));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Entries_ListsMediaOnly_NewestFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prismos-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var older = Path.Combine(dir, "older.jpg");
            var newer = Path.Combine(dir, "newer.mp4");
            var ignored = Path.Combine(dir, "notes.txt");
            File.WriteAllBytes(older, [1]);
            File.WriteAllBytes(newer, [1]);
            File.WriteAllBytes(ignored, [1]);
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

            var entries = WallpaperLibrary.Entries(dir);
            Assert.Equal(2, entries.Count);
            Assert.Equal("newer", entries[0].Title);
            Assert.Equal("video", entries[0].Type);
            Assert.Equal("image", entries[1].Type);
            Assert.All(entries, e => Assert.True(e.IsLocal));

            Assert.Empty(WallpaperLibrary.Entries(null));
            Assert.Empty(WallpaperLibrary.Entries(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ImageStill_IsGeneratedOnceAndCached()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prismos-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "photo.jpg");
        using (var bitmap = new System.Drawing.Bitmap(64, 32))
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.Teal);
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Jpeg);
        }

        var service = new GalleryService();
        var entry = LocalEntry(file);
        var cachePath = service.ThumbnailPath(entry);
        try
        {
            Assert.Null(service.LocalThumbnailIfCached(entry));
            var made = await WallpaperLibrary.EnsureThumbnailAsync(entry, service);
            Assert.Equal(cachePath, made);
            Assert.True(File.Exists(cachePath));
            // Second ask is the cache, not a second decode.
            Assert.Equal(cachePath, await WallpaperLibrary.EnsureThumbnailAsync(entry, service));
            Assert.Equal(64, entry.Width);
            Assert.Equal(32, entry.Height);
        }
        finally
        {
            try { File.Delete(cachePath); } catch { /* best effort */ }
            Directory.Delete(dir, recursive: true);
        }
    }
}
