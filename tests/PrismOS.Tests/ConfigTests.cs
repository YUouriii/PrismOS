using System.Text.Json;
using PrismOS.Config;

namespace PrismOS.Tests;

public class ConfigTests
{
    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var config = new AppConfig
        {
            Fit = FitMode.Fit,
            MuteVideo = false,
            Volume = 0.7,
            Clock = new ClockConfig
            {
                Enabled = true,
                Anchor = ClockAnchor.BottomCenter,
                TwentyFourHour = true,
                ShowSeconds = true,
                FontSize = 96,
                Color = "#80FF0000",
            },
            Pause = new PauseConfig { OnFullscreen = false },
            StartMenuShortcutAdded = true,
        };
        config.Assign(@"\\.\DISPLAY1", @"C:\wallpapers\loop.mp4");

        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

        Assert.Equal(FitMode.Fit, back.Fit);
        Assert.False(back.MuteVideo);
        Assert.Equal(0.7, back.Volume);
        Assert.Equal(ClockAnchor.BottomCenter, back.Clock.Anchor);
        Assert.True(back.Clock.TwentyFourHour);
        Assert.Equal(96, back.Clock.FontSize);
        Assert.Equal("#80FF0000", back.Clock.Color);
        Assert.False(back.Pause.OnFullscreen);
        Assert.True(back.StartMenuShortcutAdded);
        Assert.Equal(@"C:\wallpapers\loop.mp4", back.WallpaperFor(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void WallpaperFor_ExactMonitorBeatsWildcard()
    {
        var config = new AppConfig();
        config.Assign("*", @"C:\all.mp4");
        config.Assign(@"\\.\DISPLAY2", @"C:\second.mp4");

        Assert.Equal(@"C:\second.mp4", config.WallpaperFor(@"\\.\DISPLAY2"));
        Assert.Equal(@"C:\all.mp4", config.WallpaperFor(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void Assign_OverwritesExistingEntry()
    {
        var config = new AppConfig();
        config.Assign("*", @"C:\a.mp4");
        config.Assign("*", @"C:\b.mp4");

        Assert.Single(config.Wallpapers);
        Assert.Equal(@"C:\b.mp4", config.WallpaperFor(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void WallpaperFor_EmptyConfig_ReturnsNull() =>
        Assert.Null(new AppConfig().WallpaperFor(@"\\.\DISPLAY1"));

    [Fact]
    public void Defaults_AreSensible()
    {
        var config = new AppConfig();
        Assert.Equal(FitMode.Fill, config.Fit);
        Assert.True(config.MuteVideo);
        Assert.True(config.Clock.Enabled);
        Assert.True(config.Pause.OnFullscreen);
    }

    [Fact]
    public void EnumSerializedAsString()
    {
        var json = JsonSerializer.Serialize(new AppConfig { Fit = FitMode.Stretch }, ConfigJsonContext.Default.AppConfig);
        Assert.Contains("\"Stretch\"", json);
    }

    /// <summary>The file is split across AppConfig.cs, Settings.cs, DeviceBehaviour.cs and
    /// ConfigStore.cs purely for reading. The JSON shape is a promise to every config already on
    /// disk, so pin it: flat, and no section renamed or nested behind a new object.</summary>
    [Fact]
    public void JsonShapeStaysFlat()
    {
        var json = JsonSerializer.Serialize(new AppConfig(), ConfigJsonContext.Default.AppConfig);
        using var doc = JsonDocument.Parse(json);

        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(
            ["wallpapers", "fit", "muteVideo", "volume", "liveVideoPreview", "clock", "info",
             "pause", "widgets", "startMenuShortcutAdded"],
            names);
        Assert.False(doc.RootElement.TryGetProperty("behaviour", out _));
        Assert.False(doc.RootElement.TryGetProperty("settings", out _));
    }
}

/// <summary>The info section's defaults and round trip. The text-line widget was removed;
/// MaxCharacters still bounds the record's font string and stays round-trippable.</summary>
public class InfoConfigTests
{
    [Fact]
    public void ConfigWrittenBeforeTheWidgetExisted_Loads()
    {
        // The upgrade path, asserted rather than assumed: a config.json from v0.1.x has no
        // "info" key at all, and must still load without throwing.
        const string old = """
        { "wallpapers": [], "fit": "Fill", "clock": { "enabled": true } }
        """;
        var loaded = JsonSerializer.Deserialize(old, ConfigJsonContext.Default.AppConfig)!;
        Assert.NotNull(loaded.Info);
        Assert.Equal(48, loaded.Info.MaxCharacters);
    }

    [Fact]
    public void InfoConfig_SurvivesARoundTrip()
    {
        var config = new AppConfig();
        config.Info.MaxCharacters = 30;
        config.Info.Disc.MeterSensitivity = 1.75f;
        config.Info.Disc.OnlineArtwork = false;

        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

        Assert.Equal(30, back.Info.MaxCharacters);
        Assert.Equal(1.75f, back.Info.Disc.MeterSensitivity, 3);
        Assert.False(back.Info.Disc.OnlineArtwork);
        Assert.True(back.Info.Disc.ShowProgress);
    }
}

/// <summary>The halo's defaults. Ember, and on — unlike the widget it attaches to, which is off
/// by default. That exception is deliberate and recorded in the design.</summary>
public class HaloConfigTests
{
    [Fact]
    public void Halo_DefaultsToRedOrangeGreen()
    {
        // The one place literal colours belong: this test exists to pin the shipped defaults.
        // Green above the mid threshold is the conventional battery reading; the warm Ember ramp
        // is still selectable as a preset.
        var h = new AppConfig().Info.Halo;
        Assert.Equal("#FF4D4D", h.LowColor);
        Assert.Equal("#FF9A3C", h.MidColor);
        Assert.Equal("#5FD98A", h.HighColor);
        Assert.Equal("#7CE8A4", h.ChargedColor);
    }

    [Fact]
    public void Halo_DefaultsToAttachedOnTheLeft()
    {
        var h = new AppConfig().Info.Halo;
        Assert.False(h.Detached);
        Assert.Equal(HaloPlacement.Left, h.Placement);
    }

    [Fact]
    public void Halo_DefaultThresholdsAre20And50()
    {
        var h = new AppConfig().Info.Halo;
        Assert.Equal(20, h.LowThreshold);
        Assert.Equal(50, h.MidThreshold);
    }

    [Fact]
    public void ConfigWrittenBeforeTheHaloExisted_LoadsWithEmberDefaults()
    {
        const string old = """
        { "wallpapers": [], "info": { "enabled": true } }
        """;
        var loaded = JsonSerializer.Deserialize(old, ConfigJsonContext.Default.AppConfig)!;
        Assert.NotNull(loaded.Info.Halo);
        Assert.True(loaded.Info.Halo.Enabled);
        Assert.Equal("#FF4D4D", loaded.Info.Halo.LowColor);
    }

    [Fact]
    public void HaloConfig_SurvivesARoundTrip()
    {
        var config = new AppConfig();
        config.Info.Halo.Detached = true;
        config.Info.Halo.Anchor = ClockAnchor.BottomRight;
        config.Info.Halo.Size = 52;

        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;

        Assert.True(back.Info.Halo.Detached);
        Assert.Equal(ClockAnchor.BottomRight, back.Info.Halo.Anchor);
        Assert.Equal(52, back.Info.Halo.Size);
    }
}

public class ConfigStoreTests
{
    [Fact]
    public void Load_InvalidJson_ReportsItUnreadable_AndLeavesTheFile()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fw-config-").FullName, "config.json");
        const string typo = "{ \"fit\": \"Fit\", }";
        File.WriteAllText(path, typo);

        var config = ConfigStore.Load(path, out bool unreadable);

        Assert.True(unreadable);
        Assert.NotNull(config);
        Assert.Equal(typo, File.ReadAllText(path));
    }

    [Fact]
    public void Load_MissingFile_IsNotUnreadable()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fw-config-").FullName, "config.json");

        ConfigStore.Load(path, out bool unreadable);

        Assert.False(unreadable);
    }
}
