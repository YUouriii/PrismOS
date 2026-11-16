using System.Drawing;
using Microsoft.Win32;

namespace PrismOS.Widgets;

/// <summary>Artwork for the music pill when the media session has none. Browsers that play a
/// short video often publish no Thumbnail at all — Brave on TikTok is one — so the fallback is the
/// source app's own icon, resolved from the AUMID or the App Paths registry entry. Spotify and most
/// players do send a real thumbnail, which always wins over this.</summary>
public static class MusicArt
{
    public static Bitmap? ForSource(string? aumid, string? friendlyName)
    {
        string? path = Executable(aumid, friendlyName);
        if (path is null) return null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            return icon?.ToBitmap();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Some browser sessions publish the site or app icon as their "artwork". Compare the
    /// decoded content downscaled to a fixed grid so a scaled-up icon still matches the app icon.</summary>
    public static bool SameBitmap(Bitmap? a, Bitmap? b)
    {
        if (a is null || b is null) return false;
        try
        {
            using var smallA = Resize(a, 16, 16);
            using var smallB = Resize(b, 16, 16);
            var ra = new Rectangle(0, 0, smallA.Width, smallA.Height);
            var rb = new Rectangle(0, 0, smallB.Width, smallB.Height);
            var format = System.Drawing.Imaging.PixelFormat.Format32bppArgb;
            var da = smallA.LockBits(ra, System.Drawing.Imaging.ImageLockMode.ReadOnly, format);
            var db = smallB.LockBits(rb, System.Drawing.Imaging.ImageLockMode.ReadOnly, format);
            try
            {
                if (da.Stride != db.Stride) return false;
                int bytes = da.Stride * smallA.Height;
                unsafe
                {
                    byte* pa = (byte*)da.Scan0, pb = (byte*)db.Scan0;
                    long total = 0;
                    for (int i = 0; i < bytes; i++) total += Math.Abs(pa[i] - pb[i]);
                    // A flat icon scaled to a different size differs by single digits per byte;
                    // a different picture differs by hundreds.
                    return total / bytes < 6;
                }
            }
            finally
            {
                smallA.UnlockBits(da);
                smallB.UnlockBits(db);
            }
        }
        catch { return false; }
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, width, height);
        return result;
    }

    private static string? Executable(string? aumid, string? friendlyName)
    {
        if (!string.IsNullOrWhiteSpace(aumid) && File.Exists(aumid)) return aumid;

        var names = new List<string>();
        foreach (var value in new[] { aumid, friendlyName })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            string token = value.Trim();
            // AUMIDs for packaged apps look like "Package.Family!App"; the last segment is the app.
            int bang = token.LastIndexOf('!');
            if (bang >= 0 && bang + 1 < token.Length) token = token[(bang + 1)..];
            token = Path.GetFileName(token.Replace('\\', '/'));
            if (token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                token = token[..^4];
            if (token.Length > 1) names.Add(token);
        }

        string[] roots =
        [
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
        ];
        foreach (string name in names)
            foreach (string root in roots)
                if (Registry.GetValue($@"{root}\{name}.exe", "", null) is string path && File.Exists(path))
                    return path;

        return null;
    }
}
