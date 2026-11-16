using System.Drawing;
using System.Drawing.Imaging;
using PrismOS.Widgets;

namespace PrismOS.Tests;

/// <summary>The now-playing pill's geometry and paint. The real meter used to have a negative bar
/// width — twelve bands in a 16 px column — so it drew nothing at all; these pin the column, the
/// bars and the artwork placeholder so that cannot come back silently.</summary>
public class MusicStripTests
{
    private static readonly NowPlayingReading Track = new("Bairan", "Banjaare", true, "t1", 0.42d);
    private static readonly Color Accent = Color.FromArgb(255, 0xC0, 0x8B, 0xFF);

    private static MusicStrip.Metrics Measure(float scale = 1f, float maxWidth = float.MaxValue, string? title = null)
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return MusicStrip.Measure(g, title is null ? Track : Track with { Title = title }, "Spotify", scale, maxWidth);
    }

    private static (Bitmap Bitmap, MusicStrip.Metrics Metrics) Render(float[]? levels, bool playing = true,
                                                                      double progress = 0.42d, float scale = 1f)
    {
        var reading = Track with { IsPlaying = playing, Progress = progress };
        using var measure = Graphics.FromHwnd(IntPtr.Zero);
        var m = MusicStrip.Measure(measure, reading, "Spotify", scale);
        var bmp = new Bitmap(m.Size.Width, m.Size.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        MusicStrip.Paint(g, m, reading, null, scale, 0.5f, 216, levels, Accent);
        return (bmp, m);
    }

    private static bool IsBright(Color c) => c.R > 200 && c.G > 200 && c.B > 200;

    private static int BrightPixels(Bitmap bmp, Rectangle box, float fromTop = 0f, float through = 1f)
    {
        var area = new Rectangle(box.X, box.Y + (int)(box.Height * fromTop),
            box.Width, Math.Max(1, (int)(box.Height * (through - fromTop))));
        area.Intersect(new Rectangle(Point.Empty, bmp.Size));
        int count = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (IsBright(bmp.GetPixel(x, y))) count++;
        return count;
    }

    [Fact]
    public void EveryBox_IsInsideThePill()
    {
        var m = Measure();
        var total = new Rectangle(Point.Empty, m.Size);
        foreach (var box in new[] { m.ArtBox, m.EqBox, m.PrevBox, m.PlayBox, m.NextBox })
            Assert.True(total.Contains(box), $"{box} is outside the {total} pill");
    }

    [Fact]
    public void Equalizer_HasAUsableColumn()
    {
        var m = Measure();
        Assert.True(m.EqBox.Width >= 24, $"equalizer column is {m.EqBox.Width}px wide");
        Assert.True(m.EqBox.Height >= 20, $"equalizer column is {m.EqBox.Height}px tall");
        Assert.True(m.EqBox.Left > m.ArtBox.Right, "the meter must not sit over the artwork");
        Assert.True(m.EqBox.Right < m.PrevBox.Left, "the meter must not sit under the transport");
    }

    [Fact]
    public void Equalizer_ScalesWithThePill()
    {
        var one = Measure();
        var two = Measure(2f);
        Assert.InRange(two.EqBox.Width, one.EqBox.Width * 1.9, one.EqBox.Width * 2.1);
        Assert.InRange(two.EqBox.Height, one.EqBox.Height * 1.9, one.EqBox.Height * 2.1);
    }

    [Fact]
    public void Trimming_DoesNotSplitSurrogatePairs()
    {
        // 'W' fills the column and the headphones are outside Latin, so trimming realises here.
        var m = Measure(maxWidth: 140f, title: new string('W', 10) + "\U0001F3A7\U0001F3A7\U0001F3A7");
        Assert.EndsWith("…", m.TitleText);
        Assert.False(HasLoneSurrogate(m.TitleText), $"\"{m.TitleText}\" contains half a surrogate pair");
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void LiveBands_RaiseTheBars()
    {
        var hot = new float[] { 0.95f, 0.8f, 0.6f, 0.7f, 0.5f, 0.9f, 0.75f, 0.65f, 0.55f, 0.85f, 0.7f, 0.6f };
        var (loud, m) = Render(hot);
        using (loud)
        {
            var (quiet, _) = Render(new float[hot.Length]);
            using (quiet)
            {
                int litLoud = BrightPixels(loud, m.EqBox, 0f, 0.5f);
                int litQuiet = BrightPixels(quiet, m.EqBox, 0f, 0.5f);
                Assert.True(litLoud > litQuiet + 20,
                    $"the meter did not react: {litLoud} lit pixels against {litQuiet}");
            }
        }
    }

    [Fact]
    public void PausedBars_SettleLow()
    {
        var hot = new float[] { 0.95f, 0.8f, 0.6f, 0.7f, 0.5f, 0.9f, 0.75f, 0.65f, 0.55f, 0.85f, 0.7f, 0.6f };
        var (paused, m) = Render(hot, playing: false);
        using (paused)
            Assert.Equal(0, BrightPixels(paused, m.EqBox, 0f, 0.5f));
    }

    [Fact]
    public void NoArtwork_DrawsTheNoteGlyph()
    {
        var (bmp, m) = Render(null);
        using (bmp)
            Assert.True(BrightPixels(bmp, m.ArtBox) > 60, "the empty artwork tile is missing its note");
    }

    [Fact]
    public void ProgressLine_AppearsOnlyWhenTheLengthIsKnown()
    {
        var (withLine, m) = Render(null, progress: 0.42d);
        using (withLine)
        {
            var (without, _) = Render(null, progress: 0d);
            using (without)
            {
                var band = new Rectangle(0, m.Size.Height - 9, m.Size.Width, 9);
                int differing = 0;
                for (int y = band.Top; y < band.Bottom; y++)
                    for (int x = band.Left; x < band.Right; x++)
                        if (withLine.GetPixel(x, y) != without.GetPixel(x, y)) differing++;

                Assert.True(differing > 100, $"progress line drew almost nothing ({differing} px)");
            }
        }
    }
}
