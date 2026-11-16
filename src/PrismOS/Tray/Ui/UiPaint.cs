using System.Drawing;
using System.Drawing.Drawing2D;

namespace PrismOS.Tray;

/// <summary>Drawing shared by the panel's own controls, so a radius or a hover rule is stated once.
///
/// The panel had always been stock WinForms: square cards, square checkbox ticks, a flat button
/// for the selected tab. These helpers exist because the look is now painted rather than themed,
/// and three copies of the same rounded-rectangle maths would drift apart by a pixel each time.</summary>
internal static class UiPaint
{
    public const int CardRadius = 12;
    public const int ControlRadius = 7;

    /// <summary>A rounded rectangle. The radius is clamped to the shorter side so a 20 px tall
    /// control never gets a 24 px corner and an invalid path.</summary>
    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;

        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 2)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A straight blend between two colours; <paramref name="amount"/> is the share of
    /// <paramref name="to"/>.</summary>
    public static Color Mix(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * amount),
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    /// <summary>Black or white, whichever the fill can carry. The accent is a light blue in the
    /// dark palette and a deep blue in the light one, so a fixed white label loses half its
    /// contrast in one of them.</summary>
    public static Color ReadableOn(Color background) =>
        background.GetBrightness() > 0.55f ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;

    /// <summary>Lifts a fill toward white on hover and drops it on press, the way every Windows
    /// control answers the pointer. Used instead of two more palette entries per state.</summary>
    public static Color State(Color fill, bool hover, bool pressed)
    {
        if (pressed) return Mix(fill, Color.Black, Theme.IsDark ? 0.14f : 0.08f);
        if (hover) return Mix(fill, Color.White, Theme.IsDark ? 0.10f : 0.06f);
        return fill;
    }
}
