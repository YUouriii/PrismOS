using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PrismOS.Gallery;

namespace PrismOS.Tray;

/// <summary>Images for the gallery, kept small.
///
/// A still arrives as a 960 px JPEG and a card shows it at 124×70. Holding the full image per
/// entry would be megabytes of bitmaps for a list; these are downscaled once and the file is read
/// through a stream rather than <see cref="Image.FromFile"/>, which would keep the cache locked
/// open for the life of the window.</summary>
internal static class GalleryImages
{
    public static Bitmap? Load(string path, int maxWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var decoded = Image.FromStream(stream);
            var scale = Math.Min(1.0, maxWidth / (double)Math.Max(decoded.Width, 1));
            int width = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            int height = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(decoded, 0, 0, width, height);
            }
            return bitmap;
        }
        catch (Exception ex)
        {
            Common.Log.Warn($"Gallery image could not be read: {ex.Message}");
            return null;
        }
    }
}

/// <summary>One entry in the gallery list: still, name, what it is, and whether it is already on
/// disk. Clicking picks it; the preview is the form's job.</summary>
internal sealed class GalleryCard : Control
{
    private const int ThumbWidth = 116;
    private const int ThumbHeight = 66;
    private const int ThumbRadius = 8;

    private Bitmap? _thumb;
    private bool _hover;

    public GalleryCard(GalleryEntry entry)
    {
        Entry = entry;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Height = 88;
    }

    public GalleryEntry Entry { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Downloaded { get; set; }

    /// <summary>The cached still, or null while it is still being fetched.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Bitmap? Thumbnail
    {
        get => _thumb;
        set
        {
            _thumb?.Dispose();
            _thumb = value;
            Invalidate();
        }
    }

    public event EventHandler? Picked;

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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Picked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var card = UiPaint.Rounded(bounds, UiPaint.CardRadius);
        using (var fill = new SolidBrush(Selected
            ? UiPaint.Mix(Theme.Colors.Card, Theme.Colors.Accent, 0.16f)
            : _hover ? Theme.Colors.Hover : Theme.Colors.Card))
        {
            g.FillPath(fill, card);
        }
        using (var edge = new Pen(Selected ? UiPaint.Mix(Theme.Colors.Border, Theme.Colors.Accent, 0.55f) : Theme.Colors.Border))
        {
            g.DrawPath(edge, card);
        }

        DrawThumb(g);
        DrawText(g);
    }

    private void DrawThumb(Graphics g)
    {
        var area = new Rectangle(10, 11, ThumbWidth, ThumbHeight);
        using var clip = UiPaint.Rounded(area, ThumbRadius);
        g.SetClip(clip);
        if (_thumb is not null)
        {
            // Cover the box rather than stretch: a panorama in a 16:10 frame is cropped, not
            // squashed, exactly as it would be on the desktop.
            var scale = Math.Max((float)ThumbWidth / _thumb.Width, (float)ThumbHeight / _thumb.Height);
            int w = Math.Max(1, (int)(_thumb.Width * scale));
            int h = Math.Max(1, (int)(_thumb.Height * scale));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_thumb, area.X + (ThumbWidth - w) / 2, area.Y + (ThumbHeight - h) / 2, w, h);
        }
        else
        {
            g.Clear(Theme.Colors.Preview);
            using var pen = new Pen(Theme.Colors.Subtle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Entry.Type == "video")
            {
                var screen = new Rectangle(area.X + ThumbWidth / 2 - 15, area.Y + ThumbHeight / 2 - 11, 30, 22);
                g.DrawRectangle(pen, screen);
                var play = new PointF[]
                {
                    new(screen.X + 11, screen.Y + 5),
                    new(screen.X + 11, screen.Y + screen.Height - 5),
                    new(screen.X + screen.Width - 9, screen.Y + screen.Height / 2),
                };
                using var fill = new SolidBrush(Theme.Colors.Subtle);
                g.FillPolygon(fill, play);
            }
            else
            {
                var frame = new Rectangle(area.X + ThumbWidth / 2 - 13, area.Y + ThumbHeight / 2 - 11, 26, 22);
                g.DrawRectangle(pen, frame);
                using var fill = new SolidBrush(Theme.Colors.Subtle);
                g.FillEllipse(fill, frame.X + 5, frame.Y + 4, 5, 5);
                var mountain = new PointF[]
                {
                    new(frame.X + 3, frame.Y + frame.Height - 4),
                    new(frame.X + 11, frame.Y + 9),
                    new(frame.X + 19, frame.Y + frame.Height - 4),
                };
                g.FillPolygon(fill, mountain);
            }
        }
        g.ResetClip();
        using var edge = new Pen(Theme.Colors.Border);
        g.DrawPath(edge, clip);
    }

    private void DrawText(Graphics g)
    {
        int x = 10 + ThumbWidth + 12;
        int w = Width - x - 10;
        if (w < 60) return;

        TextRenderer.DrawText(g, Entry.Title, new Font("Segoe UI Semibold", 10f),
            new Rectangle(x, 11, w, 20), Theme.Colors.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, $"{Entry.KindLabel} · {Entry.ResolutionLabel} · {Entry.SizeLabel}",
            Font, new Rectangle(x, 32, w, 16), Theme.Colors.Subtle,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        var line = Downloaded ? "On this PC" : Entry.License;
        TextRenderer.DrawText(g, line, Font, new Rectangle(x, 52, w, 16),
            Downloaded ? Theme.Colors.Accent : Theme.Colors.Subtle,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>The All / Photos / Videos switch. A pill rather than a radio button: it has to be
/// read while the pointer is already over it.</summary>
internal sealed class GalleryChip : Control
{
    private bool _hover, _selected;

    public GalleryChip(string label, GalleryKind kind)
    {
        Label = label;
        Kind = kind;
        Height = 26;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public string Label { get; }

    public GalleryKind Kind { get; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public event EventHandler? Picked;

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

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) Picked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Width = TextRenderer.MeasureText(Label, Font).Width + 24;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = Selected ? Theme.Colors.Accent
            : _hover ? Theme.Colors.Hover : Theme.Colors.Control;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiPaint.Rounded(bounds, UiPaint.ControlRadius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (!Selected)
            using (var pen = new Pen(Theme.Colors.Border)) g.DrawPath(pen, path);

        var text = Selected ? UiPaint.ReadableOn(Theme.Colors.Accent) : Theme.Colors.Subtle;
        TextRenderer.DrawText(g, Label, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>The search box. Stock <see cref="TextBox"/> with no border inside a rounded panel,
/// because a bordered box in a borderless field draws a second, square outline around it.</summary>
internal sealed class SearchField : Panel
{
    private const string Placeholder = "Search wallpapers…";

    private readonly TextBox _box;
    private bool _placeholder = true;

    public SearchField()
    {
        BackColor = Theme.Colors.Control;

        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Colors.Control,
            ForeColor = Theme.Colors.Subtle,
            Font = new Font("Segoe UI", 9.5f),
            Text = Placeholder,
        };
        _box.GotFocus += (_, _) =>
        {
            if (_placeholder)
            {
                _box.Text = "";
                _box.ForeColor = Theme.Colors.Text;
                _placeholder = false;
            }
        };
        _box.LostFocus += (_, _) =>
        {
            if (_box.Text.Length == 0)
            {
                _box.Text = Placeholder;
                _box.ForeColor = Theme.Colors.Subtle;
                _placeholder = true;
            }
        };
        Controls.Add(_box);
        Height = 30;
    }

    /// <summary>The search text, with the placeholder text excluded.</summary>
    public string Value => _placeholder ? "" : _box.Text;

    /// <summary>True while the field has the keyboard focus, so the form leaves Enter and the
    /// arrows to the field instead of moving the selection.</summary>
    public bool Editing => _box.Focused;

    public event EventHandler? Changed
    {
        add => _box.TextChanged += value;
        remove => _box.TextChanged -= value;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiPaint.Rounded(bounds, UiPaint.ControlRadius);
        using var pen = new Pen(_box.Focused ? Theme.Colors.Accent : Theme.Colors.Border);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // Layout fires on every size change, including the one the constructor triggers before
        // the field exists.
        if (_box is null) return;
        int height = _box.PreferredHeight;
        _box.SetBounds(9, (Height - height) / 2, Math.Max(1, Width - 16), height);
    }
}
