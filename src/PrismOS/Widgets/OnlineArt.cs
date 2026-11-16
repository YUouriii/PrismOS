using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Text.Json;
using PrismOS.Common;

namespace PrismOS.Widgets;

/// <summary>Last resort for the art box: look the track up by title and artist on iTunes' public
/// search API, which needs no key. Used only when the media session publishes no Thumbnail — many
/// browser players don't — and a real artist is known, so a bare video title does not pull an
/// unrelated cover. Any failure is silent: the source-app icon is the next fallback.</summary>
public static class OnlineArt
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<Bitmap?> Find(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
        try
        {
            string term = Uri.EscapeDataString($"{artist.Trim()} {title.Trim()}");
            string json = await Client.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=1");
            using var doc = JsonDocument.Parse(json);
            var results = doc.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0) return null;
            string? url = results[0].GetProperty("artworkUrl100").GetString();
            if (string.IsNullOrWhiteSpace(url)) return null;

            url = url.Replace("100x100bb", "300x300bb", StringComparison.OrdinalIgnoreCase);
            byte[] bytes = await Client.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            using var decoded = Image.FromStream(stream);
            return new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            Log.Warn($"Online artwork unavailable: {ex.GetType().Name}");
            return null;
        }
    }
}
