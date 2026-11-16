using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace PrismOS.Tray;

/// <summary>A rounded colour sample that opens a picker, replacing a flat button whose BackColor
/// was the only thing saying what it did. A translucent colour is drawn over a small checker so
/// "80% white" does not look identical to "solid pale grey".</summary>
public sealed class ColorChip : Control
{
    private Color _swatch = Color.Transparent;
    private bool _hover;

    public ColorChip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(58, 26);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    /// <summary>The colour shown, alpha included. Transparent means "inherit" and is drawn as an
    /// empty chip with a diagonal, not as black.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Swatch
    {
        get => _swatch;
        set
        {
            if (_swatch == value) return;
            _swatch = value;
            Invalidate();
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is not (Keys.Space or Keys.Enter)) return;
        e.Handled = true;
        OnClick(EventArgs.Empty);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Colors.Card);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiPaint.Rounded(bounds, UiPaint.ControlRadius);

        if (_swatch == Color.Transparent)
        {
            using var empty = new SolidBrush(Theme.Colors.Control);
            g.FillPath(empty, path);
            using var pen = new Pen(Theme.Colors.Subtle);
            g.DrawLine(pen, bounds.Left + 6, bounds.Bottom - 6, bounds.Right - 6, bounds.Top + 6);
        }
        else
        {
            // Checkerboard first, then the colour over it, so alpha reads as alpha.
            g.FillPath(Brushes.White, path);
            const int cell = 6;
            using (var path2 = UiPaint.Rounded(bounds, UiPaint.ControlRadius))
            using (var shade = new SolidBrush(Color.FromArgb(26, 0, 0, 0)))
            {
                var clip = g.Save();
                g.SetClip(path2);
                for (int y = bounds.Top; y < bounds.Bottom; y += cell)
                    for (int x = bounds.Left; x < bounds.Right; x += cell)
                        if (((x - bounds.Left) / cell + (y - bounds.Top) / cell) % 2 == 0)
                            g.FillRectangle(shade, x, y, cell, cell);
                g.Restore(clip);
            }
            using (var brush = new SolidBrush(_swatch)) g.FillPath(brush, path);
        }

        using (var pen = new Pen(_hover ? Theme.Colors.Accent : Theme.Colors.Border)) g.DrawPath(pen, path);
        if (Focused && ShowFocusCues)
            using (var pen = new Pen(Theme.Colors.Accent))
                g.DrawPath(pen, UiPaint.Rounded(new Rectangle(3, 3, Width - 7, Height - 7), UiPaint.ControlRadius - 2));
    }
}
