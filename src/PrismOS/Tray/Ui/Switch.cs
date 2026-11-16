using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace PrismOS.Tray;

/// <summary>A boolean drawn as a track and a knob, rather than a tick in a box.
///
/// Same contract as the <see cref="CheckBox"/> it replaces — <see cref="Checked"/> and
/// <see cref="CheckedChanged"/> — so every page keeps its wiring and only the pixels change.
/// It is a real control rather than a painted label: focusable, driven by Space or Enter, and it
/// reports itself to screen readers as a check button with its state.</summary>
public sealed class Switch : Control
{
    private bool _checked;
    private bool _hover;

    public Switch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(46, 24);
        BackColor = Theme.Colors.Card;
        ForeColor = Theme.Colors.Text;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    protected override Size DefaultSize => new(46, 24);

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
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
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var track = new Rectangle(1, 2, Width - 3, Height - 5);
        int radius = track.Height / 2;
        var fill = _checked
            ? UiPaint.State(Theme.Colors.Accent, _hover, false)
            : UiPaint.State(Theme.Colors.Track, _hover, false);
        using (var path = UiPaint.Rounded(track, radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        int knob = track.Height - 6;
        int x = _checked ? track.Right - knob - 3 : track.Left + 3;
        // A white knob on both themes: on the light palette's darker blue it reads as a switch,
        // and on the dark palette's lighter blue it is the only high-contrast option.
        using (var brush = new SolidBrush(Color.White))
            g.FillEllipse(brush, x, track.Top + 3, knob, knob);

        if (Focused && ShowFocusCues)
            using (var pen = new Pen(UiPaint.Mix(Theme.Colors.Accent, Theme.Colors.Text, 0.25f)))
                g.DrawPath(pen, UiPaint.Rounded(new Rectangle(0, 1, Width - 1, Height - 3), radius));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessibleObject(this);

    private sealed class SwitchAccessibleObject(Switch owner) : ControlAccessibleObject(owner)
    {
        public override string? DefaultAction => "Toggle";

        public override AccessibleRole Role => AccessibleRole.CheckButton;

        public override AccessibleStates State =>
            base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);
    }
}
