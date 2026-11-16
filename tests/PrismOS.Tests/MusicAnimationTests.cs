using System.Drawing;
using System.Drawing.Imaging;
using PrismOS.Config;
using PrismOS.Widgets.Themes;

namespace PrismOS.Tests;

/// <summary>The group styles used to read the clock inside their paint closures while Classic and
/// Editorial were driven by their overlay's timer. Pin the shared phase path so a group style
/// cannot silently ship a static meter again.</summary>
public class MusicAnimationTests
{
    public static TheoryData<WidgetTheme> Themes()
    {
        var data = new TheoryData<WidgetTheme>();
        foreach (var theme in Enum.GetValues<WidgetTheme>().Where(WidgetsConfig.IsGroup)) data.Add(theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryGroupTheme_AnimatesItsMeter(WidgetTheme theme)
    {
        Bitmap At(float phase)
        {
            var input = Group.Input() with { Phase = phase };
            var panel = GroupThemes.For(theme).Layout(input).First(p => p.HasStrip);
            var bmp = new Bitmap(panel.Bounds.Width, panel.Bounds.Height);
            using (var g = Graphics.FromImage(bmp)) panel.Paint(g);
            return bmp;
        }

        using var first = At(0f);
        using var later = At(0.37f);
        int differing = 0;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != later.GetPixel(x, y)) differing++;

        Assert.True(differing > 20, $"{theme}: only {differing} pixels changed for a new phase");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryGroupTheme_FrostsItsMusicCard(WidgetTheme theme)
    {
        var panel = GroupThemes.For(theme).Layout(Group.Input()).First(p => p.HasStrip);
        var regions = panel.CardRegions;
        Assert.NotNull(regions);
        Assert.NotEmpty(regions!);
        foreach (var region in regions!)
            Assert.True(new RectangleF(Point.Empty, panel.Bounds.Size).Contains(region.Bounds),
                $"{theme}: frost region {region.Bounds} outside panel {panel.Bounds.Size}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlassDock_CentresTheMusicCard(bool withBattery)
    {
        var input = Group.Input() with { Accent = Color.Red, Battery = withBattery ? Group.Battery : null };
        var panel = GroupThemes.For(WidgetTheme.GlassDock).Layout(input).Single();
        using var bmp = new Bitmap(panel.Bounds.Width, panel.Bounds.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 140, 190, 235));
            panel.Paint(g);
        }

        // The accent-tinted artwork tile is the leftmost red region; the card starts one internal
        // pad to its left. Its centre must sit on the capsule's centre line with or without a halo.
        // The accent-tinted artwork tile is a solid red block; the date text is red too but only a
        // few pixels wide per column. Find the first column dense enough to be the artwork.
        int artX = int.MaxValue;
        for (int x = 0; x < bmp.Width && artX == int.MaxValue; x++)
        {
            int red = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.A > 150 && c.R > 60 && c.R > c.G * 2 && c.R > c.B * 2) red++;
            }
            if (red >= 14) artX = x;
        }
        var strip = GroupThemes.PaddedStrip(input.Track!.Value, input.Source, input.Scale);
        float cardX = artX - strip.Metrics.Pad;
        float centre = cardX + strip.Metrics.Size.Width / 2f;
        Assert.True(Math.Abs(centre - panel.Bounds.Width / 2f) <= 4f,
            $"withBattery={withBattery}: artX={artX} cardX={cardX} cardW={strip.Metrics.Size.Width} centre={centre} panelW={panel.Bounds.Width}");
    }

    [Theory]
    [InlineData(WidgetTheme.Orbit)]
    [InlineData(WidgetTheme.Monolith)]
    public void FrostRegion_SitsOnThePaintedCard_NotOffsetByTheShadowPad(WidgetTheme theme)
    {
        var input = Group.Input();
        var panel = GroupThemes.For(theme).Layout(input).First(p => p.HasStrip);
        var region = Assert.Single(panel.CardRegions!);
        var strip = GroupThemes.PaddedStrip(input.Track!.Value, input.Source, input.Scale);

        // Both themes centre the padded strip in the panel. The frosted region must start where the
        // card is painted — adding the strip's shadow pad again left a dark rectangle visible
        // behind and to the right of the pill.
        double expectedX = (panel.Bounds.Width - strip.Size.Width) / 2.0;
        Assert.InRange(region.Bounds.X, expectedX - 2, expectedX + 2);
    }
}
