using System.Drawing;
using System.Windows.Forms;

namespace PrismOS.Tray;

/// <summary>A radio button used as a segmented cell (the 3x3 anchor pickers), clipped to rounded
/// corners. Appearance.Button fills the cell with its own background, so the shape is the only
/// thing the stock control cannot do.</summary>
public sealed class RoundRadio : RadioButton
{
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Clip();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Clip();
    }

    private void Clip()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = UiPaint.Rounded(new Rectangle(0, 0, Width, Height), UiPaint.ControlRadius);
        var region = new Region(path);
        var previous = Region;
        Region = region;
        previous?.Dispose();
    }
}
