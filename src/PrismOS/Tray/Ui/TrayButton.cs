using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace PrismOS.Tray;

/// <summary>A button with rounded corners, a real hover and press state, and two weights.
///
/// Stock <see cref="Button"/> with <see cref="FlatStyle.Flat"/> has square corners and no hover
/// fill; the panel's primary action and its secondary actions now differ by fill rather than by
/// border colour alone. The parent's background is painted behind the rounded shape, so the
/// corners are clean on a card and in the footer without asking for a transparent control.</summary>
public sealed class TrayButton : Button
{
    public enum Weight { Primary, Secondary }

    private bool _hover;

    public TrayButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Height = 30;
        Padding = new Padding(12, 0, 12, 0);
        ForeColor = Theme.Colors.Text;
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Weight Variant { get; set; } = Weight.Secondary;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        base.OnMouseDown(mevent);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        base.OnMouseUp(mevent);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Colors.Window);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        bool pressed = MouseButtons != MouseButtons.None && ClientRectangle.Contains(PointToClient(MousePosition));
        var fill = Variant == Weight.Primary ? Theme.Colors.Accent : Theme.Colors.Control;
        if (!Enabled) fill = UiPaint.Mix(fill, Theme.Colors.Window, 0.6f);
        else fill = UiPaint.State(fill, _hover, pressed);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = UiPaint.Rounded(bounds, UiPaint.ControlRadius))
        {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (Variant == Weight.Secondary && Enabled)
                using (var pen = new Pen(Theme.Colors.Border)) g.DrawPath(pen, path);
        }

        var colour = Variant == Weight.Primary ? UiPaint.ReadableOn(Theme.Colors.Accent)
            : Enabled ? Theme.Colors.Text
            : Theme.Colors.Subtle;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, colour,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues && Enabled)
            using (var pen = new Pen(UiPaint.Mix(colour, fill, 0.5f)))
            using (var path = UiPaint.Rounded(new Rectangle(3, 3, Width - 7, Height - 7), UiPaint.ControlRadius - 2))
                g.DrawPath(pen, path);
    }
}
