using System.Drawing.Imaging;
using System.Security.Cryptography;
using PrismOS.Common;
using PrismOS.Gallery;

namespace PrismOS.Tray;

/// <summary>The gallery's "Your files" source: a folder of the user's own images and videos,
/// presented exactly like the curated entries so the same list, preview and Set wallpaper work.
///
/// Nothing is copied or downloaded. Entries point straight at the files, and the only thing
/// written is a small still per file in the gallery's thumbnail cache — generated here, because
/// a video's first frame needs the same software decode the preview uses.</summary>
public static class WallpaperLibrary
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm" };

    public static bool IsMedia(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path)) || VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>Lists the folder's media files as gallery entries, newest first. A folder that is
    /// gone (unplugged drive) simply lists nothing rather than throwing at the window.</summary>
    public static List<GalleryEntry> Entries(string? folder)
    {
        var entries = new List<GalleryEntry>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return entries;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder).Where(IsMedia);
        }
        catch (Exception ex)
        {
            Log.Warn($"Library folder could not be read: {ex.Message}");
            return entries;
        }

        foreach (var path in files.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var entry = new GalleryEntry
            {
                Id = IdFor(path),
                Title = Path.GetFileNameWithoutExtension(path),
                Type = VideoExtensions.Contains(Path.GetExtension(path)) ? "video" : "image",
                Url = path,
                IsLocal = true,
                License = "Your file",
                Description = path,
            };
            try { entry.Bytes = new FileInfo(path).Length; } catch { /* gone mid-listing */ }
            if (entry.Type == "image") ReadImageSize(entry);
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>Stable per path, so a file keeps its cached still across sessions and moves of
    /// everything else in the folder.</summary>
    public static string IdFor(string path)
    {
        var hash = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return "lib-" + Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>Header-only dimensions: GDI+ parses the size without decoding the pixels, so a
    /// 50 MP photo costs a header read, not a bitmap.</summary>
    private static void ReadImageSize(GalleryEntry entry)
    {
        try
        {
            using var stream = File.OpenRead(entry.Url);
            using var image = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            entry.Width = image.Width;
            entry.Height = image.Height;
        }
        catch
        {
            // Unreadable header: the card shows "unknown size" until a still is made.
        }
    }

    /// <summary>The cached still for a local entry, generating it on first ask. Images are
    /// downscaled with GDI+; videos give up their first decoded frame. Best effort: a file that
    /// cannot be read leaves the card with its placeholder glyph.</summary>
    public static async Task<string?> EnsureThumbnailAsync(GalleryEntry entry, GalleryService gallery)
    {
        if (!entry.IsLocal) return await gallery.EnsureThumbnailAsync(entry);
        var path = gallery.ThumbnailPath(entry);
        if (File.Exists(path))
        {
            ReadSize(entry, path);
            // A still cached before dimensions were recorded is worth one re-decode; after that
            // the sidecar answers without touching the file.
            if (entry.Width > 0 || entry.Type != "video") return path;
        }

        try
        {
            Directory.CreateDirectory(gallery.ThumbnailDirectory);
            using var bitmap = entry.Type == "video" ? await VideoStillAsync(entry) : ImageStill(entry);
            if (bitmap is null) return null;
            var temp = path + ".tmp";
            bitmap.Save(temp, ImageFormat.Jpeg);
            File.Move(temp, path, overwrite: true);
            WriteSize(entry, path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn($"Library still failed for {entry.Title}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The source resolution beside the still, so a cached thumbnail still tells the
    /// card "1920×1080" without decoding the file again.</summary>
    private static void WriteSize(GalleryEntry entry, string stillPath)
    {
        try { File.WriteAllText(stillPath + ".size", $"{entry.Width}x{entry.Height}"); }
        catch { /* cosmetic only */ }
    }

    private static void ReadSize(GalleryEntry entry, string stillPath)
    {
        if (entry.Width > 0) return;
        try
        {
            var parts = File.ReadAllText(stillPath + ".size").Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
            {
                entry.Width = w;
                entry.Height = h;
            }
        }
        catch { /* no sidecar: the card shows unknown size */ }
    }

    private static System.Drawing.Bitmap? ImageStill(GalleryEntry entry)
    {
        using var stream = File.OpenRead(entry.Url);
        using var decoded = System.Drawing.Image.FromStream(stream);
        entry.Width = decoded.Width;
        entry.Height = decoded.Height;
        const int max = 960;
        var scale = Math.Min(1.0, max / (double)Math.Max(decoded.Width, 1));
        int width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        int height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
        var bitmap = new System.Drawing.Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(decoded, 0, 0, width, height);
        }
        return bitmap;
    }

    /// <summary>One frame, then the decoder is gone: a library of videos must not keep a decode
    /// pipeline per file alive. The frame is the preview size, which is all a still needs.</summary>
    private static async Task<System.Drawing.Bitmap?> VideoStillAsync(GalleryEntry entry)
    {
        var decoder = new CpuVideoDecoder(entry.Url);
        try
        {
            decoder.Start();
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                if (decoder.Width <= 0) continue;
                // The reader opens on the decode thread, so the file's own size is only known
                // once a frame has landed.
                entry.Width = decoder.SourceWidth;
                entry.Height = decoder.SourceHeight;
                var bitmap = new System.Drawing.Bitmap(decoder.Width, decoder.Height, PixelFormat.Format32bppRgb);
                if (decoder.TryCopyFrame(bitmap)) return bitmap;
                bitmap.Dispose();
            }
            return null;
        }
        finally
        {
            decoder.Dispose();
        }
    }
}
