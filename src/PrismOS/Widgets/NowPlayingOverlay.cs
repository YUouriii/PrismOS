using System.Drawing;
using System.Drawing.Imaging;
using Timer = System.Threading.Timer;
using PrismOS.Common;
using PrismOS.Config;
using PrismOS.Desktop;
using PrismOS.Interop;
using PrismOS.Rendering;
using PrismOS.Widgets.Themes;

namespace PrismOS.Widgets;

/// <summary>The now-playing pill on its own overlay visual: album art, source app, title, artist
/// and a live meter. There are no transport controls: the desktop surface is click-through, and
/// an invisible click window could not be made to receive clicks reliably, so buttons that looked
/// like they worked were removed rather than left as decoration.
///
/// The bars repaint on the same 30 fps timer that turns the record, and only while all of these
/// hold: something is playing, the desktop is visible and the display is on.</summary>
public sealed class NowPlayingOverlay : IDisposable
{
    private const string OverlayKey = "nowplaying";

    private readonly CompositionHost _host;
    private readonly InfoConfig _config;
    private readonly MonitorInfo _monitor;
    private readonly double _dpiScale;
    private readonly NowPlayingSource _source;
    private readonly WidgetStyle _style;
    private readonly Timer _timer;
    private readonly Lock _sync = new();

    private SIZE _size;
    private readonly Func<MonitorInfo, string?>? _backdrop;
    private readonly Func<float[]?>? _readBands;
    private readonly bool _blur;
    private bool _disposed;
    private bool _suspended;   // display off
    private bool _covered;     // a window is over the desktop
    private bool _ticking;

    public NowPlayingOverlay(CompositionHost host, InfoConfig config, MonitorInfo monitor,
                             NowPlayingSource source, double dpiScale = 1.0, WidgetStyle? style = null,
                             Func<MonitorInfo, string?>? backdrop = null, bool blur = true,
                             Func<float[]?>? readBands = null)
    {
        _host = host;
        _config = config;
        _monitor = monitor;
        _dpiScale = dpiScale > 0 ? dpiScale : 1.0;
        _source = source;
        _backdrop = backdrop;
        _readBands = readBands;
        _blur = blur;
        _style = style ?? WidgetStyle.Classic;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);

        _source.Changed += OnSourceChanged;
        try
        {
            Render();
            UpdateTimer();
        }
        catch
        {
            // The source outlives this overlay, so a throw here would leave a half-built object
            // subscribed and repainting for the life of the process.
            Dispose();
            throw;
        }
    }

    private void OnSourceChanged()
    {
        try
        {
            Render();
            UpdateTimer();
        }
        catch (Exception ex)
        {
            Log.Error("Now-playing render failed", ex);
        }
    }

    public void Refresh()
    {
        Render();
        UpdateTimer();
    }

    public void SetSuspended(bool suspended)
    {
        lock (_sync)
        {
            if (_disposed || _suspended == suspended) return;
            _suspended = suspended;
        }
        UpdateTimer();
        if (!suspended) Refresh();
    }

    /// <summary>A window is over the desktop, so nothing here is visible. Reuses the signal the
    /// engine already computes for pausing video — the widget should stop for exactly the reasons
    /// the wallpaper does.</summary>
    public void SetCovered(bool covered)
    {
        lock (_sync)
        {
            if (_disposed || _covered == covered) return;
            _covered = covered;
        }
        UpdateTimer();
    }

    /// <summary>Starts or stops the frame clock. The only place the timer is armed.</summary>
    private void UpdateTimer()
    {
        bool shouldTick;
        lock (_sync)
        {
            shouldTick = RecordSpin.ShouldTurn(_disposed, _suspended, _covered,
                                               _source.Current.IsPlaying, _size.Cx > 0 && _size.Cy > 0);
            if (shouldTick == _ticking) return;
            _ticking = shouldTick;
        }

        try
        {
            if (shouldTick) _timer.Change(0, 1000 / RecordSpin.TimerFramesPerSecond);
            else _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException) { /* raced Dispose */ }
    }

    private void Tick()
    {
        try
        {
            lock (_sync)
            {
                if (_disposed || !_ticking) return;
            }
            Render();
        }
        catch (Exception ex)
        {
            Log.Error("Now-playing tick failed", ex);
        }
    }

    private void Render()
    {
        lock (_sync)
        {
            if (_disposed || _suspended) return;

            var reading = _source.Current;
            if (DiscRenderer.Measure(_config.Disc, reading).IsEmpty)
            {
                _size = default;
                try { _host.RemoveOverlay(OverlayKey); } catch { /* host may already be gone */ }
                return;
            }

            float scale = ThemeScale;
            using var measure = Graphics.FromHwnd(IntPtr.Zero);
            var metrics = MusicStrip.Measure(measure, reading,
                NowPlayingSource.FriendlySourceName(_source.SourceApp), scale);
            int pad = (int)Math.Ceiling(ShadowRecipe.Default.Pad * Math.Max(scale, 0.05f));
            var size = new SIZE { Cx = metrics.Size.Width + pad * 2, Cy = metrics.Size.Height + pad * 2 };

            var disc = _config.Disc;
            var screenPos = ClockLayout.Position(_monitor.WorkArea, size.Cx, size.Cy, disc.Anchor,
                ClockLayout.ScaleMargin(disc.MarginLeft ?? disc.MarginX, _dpiScale),
                ClockLayout.ScaleMargin(disc.MarginTop ?? disc.MarginY, _dpiScale),
                ClockLayout.ScaleMargin(disc.MarginRight ?? disc.MarginX, _dpiScale),
                ClockLayout.ScaleMargin(disc.MarginBottom ?? disc.MarginY, _dpiScale));
            int offsetX = screenPos.X - _monitor.Bounds.Left;
            int offsetY = screenPos.Y - _monitor.Bounds.Top;

            var overlay = _host.GetOverlay(OverlayKey);
            if (overlay is null || size.Cx != _size.Cx || size.Cy != _size.Cy)
            {
                _size = size;
                overlay = _host.CreateOverlay(OverlayKey, size.Cx, size.Cy, offsetX, offsetY);
            }
            else
            {
                overlay.SetOffset(offsetX, offsetY);
            }

            using var bmp = new Bitmap(size.Cx, size.Cy, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                if (_blur && _backdrop?.Invoke(_monitor) is { } still && File.Exists(still))
                    PanelBlur.ApplyUnderPanel(g, _monitor, still,
                        new Rectangle(screenPos.X - _monitor.Bounds.Left, screenPos.Y - _monitor.Bounds.Top, size.Cx, size.Cy),
                        [new PanelBlur.Region(new RectangleF(pad, pad, metrics.Size.Width, metrics.Size.Height), metrics.Size.Height * 0.32f)]);
                g.TranslateTransform(pad, pad);
                MusicStrip.Paint(g, metrics, reading, _source.Art, scale, MusicStrip.BarPhase(), _blur ? 136 : 216,
                                 _readBands?.Invoke(), _style.Accent, _config.Disc.ShowProgress);
                g.ResetTransform();
                BakedShadow.DrawBehind(bmp, ShadowRecipe.Default, scale);
            }
            overlay.PresentBitmap(bmp);
        }
    }

    private float ThemeScale => _style.Size * (float)_dpiScale * (_config.Disc.Size / 112f);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _ticking = false;
            _source.Changed -= OnSourceChanged;
            _timer.Dispose();
            try { _host.RemoveOverlay(OverlayKey); } catch { /* host may already be gone */ }
        }
    }
}
