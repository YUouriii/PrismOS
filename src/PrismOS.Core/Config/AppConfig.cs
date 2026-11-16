using System.Text.Json.Serialization;

namespace PrismOS.Config;

public sealed class WallpaperAssignment
{
    /// <summary>Monitor device name (e.g. \\.\DISPLAY1); "*" applies to all monitors.</summary>
    public string Monitor { get; set; } = "*";
    public string Path { get; set; } = "";
}

/// <summary>Everything a person chose, and how the machine should behave while PrismOS runs.
///
/// The file is split by question, not by size: <c>Settings.cs</c> holds what the desktop looks
/// like, <c>DeviceBehaviour.cs</c> holds what the machine should do (fit, audio, when to pause),
/// and <c>ConfigStore.cs</c> owns the file itself. This class stays the single aggregate so the
/// JSON schema — and every call site — is untouched by where a section lives.</summary>
public sealed class AppConfig
{
    public List<WallpaperAssignment> Wallpapers { get; set; } = [];
    public FitMode Fit { get; set; } = FitMode.Fill;
    public bool MuteVideo { get; set; } = true;
    public double Volume { get; set; } = 0.3;
    /// <summary>Settings-panel video preview plays live in the panel. Off means it shows the
    /// captured still instead — decoding a video in the panel wakes the GPU and NVIDIA's
    /// overlay.</summary>
    public bool LiveVideoPreview { get; set; } = true;
    public ClockConfig Clock { get; set; } = new();
    public InfoConfig Info { get; set; } = new();
    public PauseConfig Pause { get; set; } = new();
    public WidgetsConfig Widgets { get; set; } = new();

    /// <summary>Set once a Start menu shortcut exists. From then on it is only repointed, never
    /// recreated, so deleting it from the Start menu sticks. Set back to false to get it again.</summary>
    public bool StartMenuShortcutAdded { get; set; }

    /// <summary>True only for the config made because no config.json existed yet. Never saved:
    /// after the first save the file exists, and the next start is not fresh.</summary>
    [JsonIgnore]
    public bool IsFreshInstall { get; set; }

    public string? WallpaperFor(string monitorDevice)
    {
        var exact = Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, monitorDevice, StringComparison.OrdinalIgnoreCase));
        var star = Wallpapers.FirstOrDefault(w => w.Monitor == "*");
        var path = (exact ?? star)?.Path;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public void Assign(string monitorDevice, string path)
    {
        var existing = Wallpapers.FirstOrDefault(w => string.Equals(w.Monitor, monitorDevice, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Path = path;
        else Wallpapers.Add(new WallpaperAssignment { Monitor = monitorDevice, Path = path });
    }

    /// <summary>Puts every setting back to how the app ships, keeping the things that are not
    /// settings.
    ///
    /// The wallpapers stay: a person who has spent an evening finding a video they like does not
    /// expect "reset" to throw it away, and the point of the button is to fix the settings, not
    /// the desktop. The Start-menu flag stays too, or the app would recreate an entry the user
    /// had deleted, and the one-time styles notice stays shown, or it would appear again for
    /// someone who has already dismissed it.
    ///
    /// What this deliberately does not touch is the wallpaper PrismOS will put back when it
    /// exits: that lives beside the log, not in the config, and losing it would leave a user
    /// with someone else's frame on their desktop.</summary>
    public void ResetToDefaults()
    {
        var defaults = new AppConfig();
        bool noticeShown = Widgets.ThemesNoticeShown;
        // Wallpapers is not touched: it is the one list here that is not a setting.
        Fit = defaults.Fit;
        MuteVideo = defaults.MuteVideo;
        Volume = defaults.Volume;
        LiveVideoPreview = defaults.LiveVideoPreview;
        Clock = defaults.Clock;
        Info = defaults.Info;
        Pause = defaults.Pause;
        Widgets = defaults.Widgets;
        Widgets.ThemesNoticeShown = noticeShown;
    }
}
