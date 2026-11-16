using System.Drawing;
using System.Windows.Forms;

namespace PrismOS.Tray;

/// <summary>A card with rounded corners and a hairline border, for the settings panel's sections.
///
/// The corners are a clipped <see cref="Region"/> rather than painted ones on purpose: children
/// inherit the card's background, so a label painted 16 px inside the padding meets card colour
/// with no seam, and nothing has to be re-coloured when a row is added. A Region's edge is not
/// anti-aliased, which at this radius on these backgrounds is not visible.</summary>
public sealed class RoundedCard : TableLayoutPanel
{
    public RoundedCard()
    {
        DoubleBuffered = true;
        BackColor = Theme.Colors.Card;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ClipCorners();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ClipCorners();
    }

    private void ClipCorners()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = UiPaint.Rounded(new Rectangle(0, 0, Width, Height), UiPaint.CardRadius);
        var region = new Region(path);
        var previous = Region;
        Region = region;
        previous?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 1 || Height <= 1) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Colors.Border);
        using var path = UiPaint.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), UiPaint.CardRadius);
        e.Graphics.DrawPath(pen, path);
    }
}
