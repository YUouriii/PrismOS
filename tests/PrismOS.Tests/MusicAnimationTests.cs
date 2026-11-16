using System.Drawing;
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
