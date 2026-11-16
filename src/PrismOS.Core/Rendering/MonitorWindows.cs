using System.Diagnostics.CodeAnalysis;
using PrismOS.Desktop;

namespace PrismOS.Rendering;

/// <summary>One <see cref="WallpaperWindow"/> per monitor, created on demand and attached to the
/// desktop layer.
///
/// Both the wallpaper and every widget ride these windows, so the registry is the single owner of
/// their lifetime. Before this, three call sites (wallpaper apply, clock, info/group) each created
/// a window and each re-stated the same three steps — wire DeviceLost, attach to the layer, ensure
/// the composition host — which is exactly the kind of duplication that drifts.</summary>
public sealed class MonitorWindows : IDisposable
{
    private readonly DesktopLayerHost _layer;
    private readonly Dictionary<string, WallpaperWindow> _windows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forwarded from every window's composition host, so callers subscribe once instead
    /// of per (re)created surface.</summary>
    public event Action? DeviceLost;

    public MonitorWindows(DesktopLayerHost layer) => _layer = layer;

    public int Count => _windows.Count;

    public IReadOnlyCollection<WallpaperWindow> Values => _windows.Values;

    /// <summary>The monitor's window, created and attached on first use. Reuse is the point: an
    /// existing window means no re-attach, no flash of the shell wallpaper, and no blink in the
    /// widgets drawn into its composition tree.</summary>
    public WallpaperWindow GetOrCreate(MonitorInfo monitor)
    {
        if (_windows.TryGetValue(monitor.Device, out var existing)) return existing;

        var window = new WallpaperWindow(monitor);
        window.DeviceLost += () => DeviceLost?.Invoke();
        _layer.Attach(window.Hwnd, monitor.Bounds);
        window.EnsureHost();
        _windows[monitor.Device] = window;
        return window;
    }

    public bool TryGet(string device, [NotNullWhen(true)] out WallpaperWindow? window) =>
        _windows.TryGetValue(device, out window);

    /// <summary>Disposes every window, its renderer and its composition host. The Media
    /// Foundation threads a disposed player strands are reclaimed here, once, rather than at each
    /// caller that happens to remember.</summary>
    public void DisposeAll()
    {
        foreach (var window in _windows.Values) window.Dispose();
        _windows.Clear();
        VideoRenderer.ReclaimMediaPipeline();
    }

    public void Dispose() => DisposeAll();
}
