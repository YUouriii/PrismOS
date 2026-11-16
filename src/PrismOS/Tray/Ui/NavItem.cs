using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace PrismOS.Tray;

/// <summary>A rail entry: the panel's page list, drawn as a quiet pill instead of a flat button.
///
/// The selected page is the only thing on the rail that is coloured, which is how the eye finds
/// "where am I" without a second accent bar competing with the text. Hover is the same pill in a
/// neutral fill, so nothing moves when the pointer crosses the rail.</summary>
public sealed class NavItem : Control
{
    private bool _selected;
    private bool _hover;
    private Font? _bold;

    public NavItem(string title)
    {
        Text = title;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Colors.Window;
        ForeColor = Theme.Colors.Text;
        Height = 38;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTab;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _bold?.Dispose();
        _bold = null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var pill = new Rectangle(8, 3, Width - 16, Height - 6);
        Color fill = _selected ? Theme.Colors.AccentSoft
            : _hover ? Theme.Colors.Hover
            : Color.Empty;
        if (fill != Color.Empty)
            using (var path = UiPaint.Rounded(pill, UiPaint.ControlRadius))
            using (var brush = new SolidBrush(fill))
                g.FillPath(brush, path);

        var colour = _selected ? Theme.Colors.Accent : Theme.Colors.Text;
        var font = _selected ? (_bold ??= new Font(Font, FontStyle.Bold)) : Font;
        TextRenderer.DrawText(g, Text, font,
            new Rectangle(pill.Left + 12, pill.Top, pill.Width - 20, pill.Height), colour,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
            using (var pen = new Pen(UiPaint.Mix(Theme.Colors.Subtle, Theme.Colors.Window, 0.5f)))
                g.DrawPath(pen, UiPaint.Rounded(pill, UiPaint.ControlRadius));
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bold?.Dispose();
        base.Dispose(disposing);
    }
}
