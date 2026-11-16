using PrismOS.Common;
using PrismOS.Config;

namespace PrismOS.Desktop;

/// <summary>What the lock-screen write did. The distinction matters: "skipped" is not an error,
/// and "needs elevation" is a one-time admin prompt, not a failure.</summary>
public enum LockScreenOutcome
{
    Skipped,
    Updated,
    NeedsElevation,
    Failed,
}

/// <summary>The lock screen shows a static picture, painted by Windows from a policy key. PrismOS
/// points that key at the still it captured, so the lock screen matches the live wallpaper's first
/// frame; the shell supplies that still and owns the tray balloons.</summary>
public sealed class LockScreenService
{
    private readonly AppConfig _config;
    private readonly Func<MonitorInfo, string?> _stillPath;
    private readonly Action<Action> _marshal;
    private readonly Action<string, string, Action?>? _balloon;
    private bool _hintShown;

    public LockScreenService(AppConfig config, Func<MonitorInfo, string?> stillPath,
        Action<Action> marshal, Action<string, string, Action?>? balloon = null)
    {
        _config = config;
        _stillPath = stillPath;
        _marshal = marshal;
        _balloon = balloon;
    }

    /// <summary>Points the policy key at the primary monitor's still. Off means the checkbox is
    /// off, not "leave the old one alone" — the key is simply never written.</summary>
    public LockScreenOutcome Apply()
    {
        if (!_config.Widgets.LockScreen) return LockScreenOutcome.Skipped;
        try
        {
            var monitors = MonitorTracker.Enumerate();
            var target = monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
            if (target is null) return LockScreenOutcome.Skipped;
            var configImage = _config.WallpaperFor(target.Device);
            if (configImage is null) return LockScreenOutcome.Skipped;

            string? source = _stillPath(target);
            if (source is null)
            {
                Log.Info("Lock screen unchanged: no usable image captured yet");
                return LockScreenOutcome.Skipped;
            }

            var result = LockScreenWallpaper.Set(source, LockScreenWallpaper.AppImagePath);
            switch (result)
            {
                case LockScreenWallpaper.Result.Ok:
                    Log.Info("Lock screen image updated from configured wallpaper");
                    return LockScreenOutcome.Updated;
                case LockScreenWallpaper.Result.NeedsElevation:
                    if (!_hintShown && _balloon is not null)
                    {
                        _hintShown = true;
                        _balloon(
                            "PrismOS isn't on the lock screen",
                            "Windows needs admin once to write the lock screen image policy. Click to elevate.",
                            () => _marshal(Elevate));
                    }
                    return LockScreenOutcome.NeedsElevation;
                default:
                    Log.Warn("Lock screen write failed; image left unchanged");
                    return LockScreenOutcome.Failed;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Lock screen apply failed: {ex.Message}");
            return LockScreenOutcome.Failed;
        }
    }

    /// <summary>Re-launches PrismOS elevated just to write the policy key; nothing else starts.
    /// UAC belongs to us, not to any helper we ship.</summary>
    public void Elevate()
    {
        var result = LockScreenWallpaper.SetViaElevation(LockScreenWallpaper.AppImagePath);
        Log.Info($"Lock screen elevation result: {result}");
        if (result == LockScreenWallpaper.Result.Ok)
            _balloon?.Invoke("Lock screen updated", "Your wallpaper now shows on the lock screen too.", null);
        else
            _balloon?.Invoke("Lock screen kept as is", "Elevation was declined or failed; see the log.", null);
    }
}
