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
}
