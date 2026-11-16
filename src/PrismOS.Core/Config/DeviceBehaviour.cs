namespace PrismOS.Config;

/// <summary>How a wallpaper meets a monitor whose aspect ratio it does not share.</summary>
public enum FitMode { Fill, Fit, Stretch }

/// <summary>When to stop decoding and drawing, so a game, a video call or a battery's last
/// percent is not competing with a wallpaper for the machine.</summary>
public sealed class PauseConfig
{
    public bool OnFullscreen { get; set; } = true;
    public bool OnBatterySaver { get; set; } = true;
    public bool OnRemoteSession { get; set; } = true;
}
