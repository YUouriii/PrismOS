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
