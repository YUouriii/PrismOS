using PrismOS.Common;
using PrismOS.Config;
using PrismOS.Desktop;
using PrismOS.Interop;

namespace PrismOS.Rendering;

/// <summary>What a wallpaper actually is: one surface window per monitor, one renderer inside it,
/// the fit and audio settings they follow, and the still frame captured from a video's first
/// settled frame so a virtual-desktop switch or the lock screen has something to paint.
///
/// It owns no widgets and no tray UI. The static frame is reported through
/// <see cref="StaticFrameCaptured"/> and the shell decides what else that means (lock screen,
/// accent sampling, rebuilding a widget), which keeps this side free of presentation code.</summary>
public sealed class WallpaperService : IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private readonly AppConfig _config;
    private readonly Action<Action> _marshal;
    private readonly Action<string, string>? _balloon;
    private string _originalWallpaper = "";

    public WallpaperService(AppConfig config, DesktopLayerHost layer, Action<Action> marshal,
        Action<string, string>? balloon = null)
    {
        _config = config;
        _marshal = marshal;
        _balloon = balloon;
        Windows = new MonitorWindows(layer);
        Windows.DeviceLost += () => DeviceLost?.Invoke();
    }

    /// <summary>The per-monitor windows and their renderers.</summary>
    public MonitorWindows Windows { get; }

    /// <summary>The wallpapers live behind the same pause policy as the widgets: a fresh renderer
    /// has to be told what the current state is, or it plays behind a maximised window.</summary>
    public Action? InvalidatePause { get; set; }

    /// <summary>Raised when a surface reports DXGI device removal or reset. The shell owns
    /// recovery because recovery rebuilds widgets too.</summary>
    public event Action? DeviceLost;

    /// <summary>Raised twice-failed playback with the file and the reason, so the shell can say
    /// what to install instead of leaving a black desktop.</summary>
    public event Action<string, string>? PlaybackFailed;

    /// <summary>Raised on the main thread once a monitor's static frame has been written and
    /// pointed at by the OS wallpaper. Carries the file that was applied, because the config may
    /// already have moved on to a newer selection by the time the frame lands.</summary>
    public event Action<MonitorInfo, string>? StaticFrameCaptured;

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    // ---- applying -----------------------------------------------------------------------------

    /// <summary>Applies the configured wallpaper on every monitor that has one. A missing file is
    /// logged and skipped rather than throwing: the config outlives the files it names.</summary>
    public void ApplyFromConfig()
    {
        foreach (var monitor in MonitorTracker.Enumerate())
        {
            var path = ExistingWallpaperFor(monitor.Device);
            if (path is null) continue;
            try
            {
                Apply(monitor, path);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to apply wallpaper on {monitor.Device}", ex);
            }
        }
    }

    /// <summary>Applies one file to the monitors a selection covers. Returns whether anything was
    /// applied — the caller only records the choice when it was.</summary>
    public bool ApplyToSelection(string path, string monitorDevice = "*")
    {
        bool anyApplied = false;
        foreach (var monitor in MonitorTracker.Enumerate())
        {
            if (monitorDevice != "*" && !string.Equals(monitor.Device, monitorDevice, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                Apply(monitor, path);
                anyApplied = true;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to apply {path} on {monitor.Device}", ex);
                _balloon?.Invoke("Couldn't set wallpaper", $"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return anyApplied;
    }

    /// <summary>Fit-mode or similar change: swaps renderers in place, reusing the window and the
    /// composition host, so there is no flicker and the widgets never blink.</summary>
    public void Refresh()
    {
        foreach (var monitor in MonitorTracker.Enumerate())
        {
            var path = ExistingWallpaperFor(monitor.Device);
            if (path is null) continue;
            try
            {
                Apply(monitor, path);
            }
            catch (Exception ex)
            {
                Log.Error($"Refresh failed on {monitor.Device}", ex);
            }
        }
    }

    public void ApplyAudioSettings()
    {
        foreach (var window in Windows.Values)
            if (window.Renderer is VideoRenderer video)
            {
                video.IsMuted = _config.MuteVideo;
                video.Volume = _config.Volume;
            }
    }

    /// <summary>The configured path when it exists, otherwise the wildcard ('*') path when that
    /// exists. A missing DISPLAY1-specific file previously blanked the monitor even though a
    /// perfectly usable catch-all was configured for everything else.</summary>
    private string? ExistingWallpaperFor(string monitorDevice)
    {
        var path = _config.WallpaperFor(monitorDevice);
        if (path is not null && File.Exists(path)) return path;

        var star = _config.Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, "*", StringComparison.Ordinal))?.Path;
        if (star is not null && !string.IsNullOrWhiteSpace(star) && star != path && File.Exists(star))
        {
            Log.Warn($"Configured wallpaper missing ({path ?? "none"}); falling back to the '*' file {star}");
            return star;
        }

        if (path is not null) Log.Warn($"Configured wallpaper missing: {path}");
        return null;
    }

    /// <summary>Lock: park every wallpaper on frame 0 before the lock screen covers it.</summary>
    public void FreezeAll()
    {
        foreach (var window in Windows.Values) window.Renderer?.FreezeFrameAtZero();
    }

    /// <summary>Unlock: ease each wallpaper back and ramp playback up again.</summary>
    public void WakeAll()
    {
        foreach (var window in Windows.Values) window.Renderer?.WakeTransition();
    }

    private void Apply(MonitorInfo monitor, string path)
    {
        var window = Windows.GetOrCreate(monitor);
        var host = window.EnsureHost();

        // When the first frame is captured, the OS static wallpaper for this monitor is pointed
        // at it so a virtual-desktop switch (or Task View) paints a matching frame instead of the
        // previous wallpaper — no flash before our live layer returns.
        var staticPath = WallpaperFiles.StaticPath(monitor.Device);
        var captured = monitor;
        Action onStatic = () => _marshal(() => OnStaticFrame(captured, path, staticPath));

        IWallpaperRenderer renderer;
        if (IsImage(path))
        {
            renderer = new ImageRenderer(host, monitor.Bounds.Width, monitor.Bounds.Height,
                _config.Fit, staticPath, onStatic);
        }
        else
        {
            var video = new VideoRenderer(host, monitor.Bounds.Width, monitor.Bounds.Height,
                _config.Fit, _config.MuteVideo, _config.Volume, staticPath, onStatic);
            video.PlaybackFailed += (failed, detail) => PlaybackFailed?.Invoke(failed, detail);
            renderer = video;
        }
        window.SetRenderer(renderer);
        renderer.Load(path);

        InvalidatePause?.Invoke(); // re-evaluate pause state for the fresh renderer
        Log.Info($"Wallpaper applied on {monitor.Device}: {path}");
    }

    private void OnStaticFrame(MonitorInfo monitor, string wallpaperPath, string staticPath)
    {
        try { DesktopWallpaper.SetForMonitor(monitor.Device, monitor.Bounds, staticPath); }
        catch (Exception ex) { Log.Error("Set static fallback failed", ex); }

        // Point every virtual desktop at this frame so switching desktops paints a matching
        // wallpaper during the slide animation instead of each desktop's own.
        if (monitor.Primary)
        {
            try { VirtualDesktopWallpaper.SetAll(staticPath); }
            catch (Exception ex) { Log.Error("Set per-desktop fallback failed", ex); }
        }

        StaticFrameCaptured?.Invoke(monitor, wallpaperPath);
    }

    // ---- the user's original wallpaper ----------------------------------------------------------

    /// <summary>Remembers the wallpaper that was there before PrismOS, once, so an exit or an
    /// uninstall can put it back. A file left by an earlier run wins: it holds the true original,
    /// while whatever is current now may be PrismOS's own static frame.</summary>
    public void SaveOriginalWallpaper()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            if (File.Exists(WallpaperFiles.SavedWallpaper))
            {
                _originalWallpaper = File.ReadAllText(WallpaperFiles.SavedWallpaper).Trim();
            }
            else
            {
                _originalWallpaper = DesktopWallpaper.GetCurrent();
                File.WriteAllText(WallpaperFiles.SavedWallpaper, _originalWallpaper);
                Log.Info($"Saved original wallpaper: {(_originalWallpaper.Length == 0 ? "(none)" : _originalWallpaper)}");
            }

            // Per-desktop wallpapers (Windows 11) — save once so we can restore them.
            if (!File.Exists(WallpaperFiles.SavedDesktopWallpapers))
            {
                var perDesktop = VirtualDesktopWallpaper.ReadAll();
                File.WriteAllLines(WallpaperFiles.SavedDesktopWallpapers,
                    perDesktop.Select(kv => $"{kv.Key}\t{kv.Value}"));
                Log.Info($"Saved {perDesktop.Count} per-desktop wallpaper(s)");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save original wallpaper: {ex.Message}");
        }
    }

    public void RestoreOriginalWallpaper() => WallpaperRestore.Restore(_originalWallpaper);

    public void Dispose() => Windows.DisposeAll();
}
