using System.Drawing.Drawing2D;
using PrismOS.Common;
using PrismOS.Config;

namespace PrismOS.Tray;

/// <summary>A live preview of the wallpaper on one display.
///
/// An image shows the image, downscaled on load. A video plays through a software Media
/// Foundation decode (see <see cref="CpuVideoDecoder"/>) and is drawn with GDI+ — no D3D device
/// is created, so the NVIDIA App overlay is not triggered. It is muted and loops.
///
/// The caption (the wallpaper's file name) is drawn along the bottom edge with a translucent
/// band, which is also what keeps it readable over bright frames.</summary>
public sealed class WallpaperPreview : Control
{
    private const int MaxPreviewPixels = 640;
    private const int CaptionHeight = 22;
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm" };

    private Bitmap? _bitmap;
    private Bitmap? _frameBitmap;
    private int _framePending;
    private CpuVideoDecoder? _decoder;
    private string? _path;
    private string _placeholder = "No wallpaper set";
    private string _caption = "";
    private FitMode _fit = FitMode.Fill;

    public WallpaperPreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                  ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        ForeColor = Theme.Colors.Text;
        Size = new Size(444, 249);
    }

    /// <summary>True for extensions the video pipeline handles. Public so the settings panel
    /// can pick a still frame instead when the live preview is off.</summary>
    public static bool IsVideoPath(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>Shown when there is no wallpaper to preview.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => _placeholder;
        set { _placeholder = value; Invalidate(); }
    }

    /// <summary>How the wallpaper is painted into the monitor — mirrors the Fit mode setting.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public FitMode Fit
    {
        get => _fit;
        set { _fit = value; Invalidate(); }
    }

    /// <summary>The file name of the wallpaper, drawn along the bottom edge.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Caption
    {
        get => _caption;
        set { _caption = value; Invalidate(); }
    }

    /// <summary>Points at a new image or video. Null clears the preview. A path that cannot be read
    /// is cleared rather than throwing: a wallpaper may be on a drive that is not plugged in.</summary>
    public void SetSource(string? path)
    {
        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;
        _path = path;

        StopDecoder();
        _bitmap?.Dispose();
        _bitmap = null;
        _frameBitmap?.Dispose();
        _frameBitmap = null;

        if (path is null)
        {
            _placeholder = "No wallpaper on this display";
            Invalidate();
            return;
        }
        if (!File.Exists(path))
        {
            _placeholder = $"Not found: {Path.GetFileName(path)}";
            Invalidate();
            return;
        }
        if (IsVideoPath(path))
        {
            // Start only when this control is really on screen. A control that has not been
            // parented yet reports Visible == true, so starting here used to spin up a 4K
            // Media Foundation decode for a page the user had not opened.
            if (IsHandleCreated && Visible) StartDecoder(path);
            _placeholder = "Loading preview…";
        }
        else
        {
            LoadImage(path);
        }
        Invalidate();
    }

    private void StartDecoder(string path)
    {
        _decoder = new CpuVideoDecoder(path);
        _decoder.FrameReady += OnFrameReady;
        _decoder.Start();
    }

    private void StopDecoder()
    {
        if (_decoder is null) return;
        try { _decoder.FrameReady -= OnFrameReady; } catch { }
        try { _decoder.Dispose(); } catch (Exception ex) { Log.Warn($"Decoder disposal failed: {ex.Message}"); }
        _decoder = null;
    }

    private void OnFrameReady()
    {
        if (IsDisposed) return;
        // A frame every 16 ms must not queue a message each time: one pending invalidate is
        // enough, and WinForms will paint once.
        if (Interlocked.Exchange(ref _framePending, 1) != 0) return;
        try
        {
            if (!IsHandleCreated)
            {
                Volatile.Write(ref _framePending, 0);
                return;
            }
            BeginInvoke((MethodInvoker)(() =>
            {
                Volatile.Write(ref _framePending, 0);
                Invalidate();
            }));
        }
        catch
        {
            Volatile.Write(ref _framePending, 0); // the panel went away mid-notification
        }
    }

    private void LoadImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var decoded = Image.FromStream(stream);
            _bitmap = Downscale(decoded, MaxPreviewPixels);
        }
        catch (Exception ex)
        {
            Common.Log.Warn($"Wallpaper preview unavailable for {path}: {ex.Message}");
            _bitmap = null;
            _placeholder = $"Could not read {Path.GetFileName(path)}";
        }
    }

    /// <summary>A copy of the image no wider than <paramref name="maxPixels"/>, so a 8K
    /// wallpaper does not cost a full-resolution GDI+ bitmap to fill a small box.</summary>
    private static Bitmap Downscale(Image source, int maxPixels)
    {
        int width = source.Width;
        int height = source.Height;
        if (width > maxPixels)
        {
            height = Math.Max(1, (int)Math.Round(height * (maxPixels / (double)width)));
            width = maxPixels;
        }
        var scaled = new Bitmap(Math.Max(1, width), Math.Max(1, height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, scaled.Width, scaled.Height));
        return scaled;
    }

    private void DrawImage(Graphics g, Image image, Rectangle area)
    {
        if (_fit == FitMode.Stretch)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, area);
            return;
        }

        double scale = _fit == FitMode.Fill
            ? Math.Max((double)area.Width / image.Width, (double)area.Height / image.Height)
            : Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
        int w = Math.Max(1, (int)Math.Round(image.Width * scale));
        int h = Math.Max(1, (int)Math.Round(image.Height * scale));

        var state = g.Save();
        g.SetClip(area, CombineMode.Intersect);
        var dest = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, dest);
        g.Restore(state);
    }

    /// <summary>Starts or stops the live decode. The Wallpaper page calls this when it is shown or
    /// left: a paused Media Foundation reader still owns its decode pipeline and a pool of
    /// full-size surfaces, so the pipeline is torn down entirely off the page and rebuilt on
    /// return.</summary>
    public void SetActive(bool active)
    {
        if (!active)
        {
            bool hadDecoder = _decoder is not null;
            StopDecoder();
            // Media Foundation frees the reader's buffers through finalization; collect off the
            // UI thread so leaving the page stays snappy but the peak does not linger.
            if (hadDecoder)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
            Invalidate();
            return;
        }

        if (_decoder is null && _path is not null && IsVideoPath(_path) && File.Exists(_path) && IsHandleCreated)
            StartDecoder(_path);
        _decoder?.Resume();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_path is null) return;
        SetActive(Visible);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        // The page becoming visible does not guarantee this control has a handle yet, so the
        // decoder is started here: OnPaint only runs when the page really is on screen.
        if (_decoder is null && _path is not null && IsVideoPath(_path) && Visible)
            StartDecoder(_path);

        int captionHeight = _caption.Length > 0 ? CaptionHeight : 0;
        var imageArea = new Rectangle(0, 0, Width, Height - captionHeight);

        // The decoder owns one pixel buffer; the panel owns one bitmap. Nothing is allocated per
        // frame, so a looping video preview is flat in memory.
        Bitmap? frame = null;
        if (_decoder is not null && _decoder.Width > 0 && _decoder.Height > 0)
        {
            if (_frameBitmap is null || _frameBitmap.Width != _decoder.Width || _frameBitmap.Height != _decoder.Height)
            {
                _frameBitmap?.Dispose();
                _frameBitmap = new Bitmap(_decoder.Width, _decoder.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            }
            if (_decoder.TryCopyFrame(_frameBitmap)) frame = _frameBitmap;
        }
        var image = (Image?)frame ?? _bitmap;
        string? error = _decoder?.Error;

        if (image is not null)
        {
            DrawImage(g, image, imageArea);
        }
        else
        {
            using var text = new SolidBrush(Theme.Colors.Subtle);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString(error is not null ? $"Could not play: {error}" : _placeholder, Font, text, imageArea, format);
        }

        if (captionHeight > 0)
        {
            using var back = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            g.FillRectangle(back, new Rectangle(0, Height - captionHeight, Width, captionHeight));
            using var text = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            using var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisPath,
            };
            g.DrawString(_caption, Font, text, new Rectangle(8, Height - captionHeight, Width - 16, captionHeight), format);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopDecoder();
            _bitmap?.Dispose();
            _bitmap = null;
            _frameBitmap?.Dispose();
            _frameBitmap = null;
        }
        base.Dispose(disposing);
    }
}
