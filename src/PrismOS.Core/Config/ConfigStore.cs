using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrismOS.Config;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AppConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;

public static class ConfigStore
{
    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrismOS");

    /// <summary>The pre-rename FeatherWall config folder, read only by <see cref="LegacyMigration"/>.</summary>
    public static string LegacyDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherWall");

    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load() => Load(ConfigPath, out _);

    /// <summary><paramref name="unreadable"/> is true when a file exists but could not be parsed,
    /// so the defaults returned are not the user's settings and must not be saved over it.</summary>
    /// <param name="ranBefore">An earlier PrismOS has run on this machine. v0.2.0 wrote
    /// config.json only when a setting changed, so a missing file alone does not make a fresh
    /// install — someone who only ever used the default clock is still upgrading, and keeps Classic.</param>
    public static AppConfig Load(string path, out bool unreadable, bool ranBefore = false)
    {
        unreadable = false;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.AppConfig) ?? new AppConfig();
            return ranBefore ? new AppConfig() : FreshInstall();
        }
        catch (Exception ex)
        {
            unreadable = true;
            Common.Log.Warn($"Config unreadable, using defaults ({ex.Message})");
        }
        return new AppConfig();
    }

    /// <summary>No config.json has ever been written, so there is no look to protect: a fresh
    /// install starts on Orbit with the music and battery widget on, so its rings have something to
    /// show. An existing file without a "widgets" section is an upgrade and loads as Classic.</summary>
    private static AppConfig FreshInstall()
    {
        var config = new AppConfig { IsFreshInstall = true };
        config.Widgets.Theme = WidgetsConfig.ConfigName(WidgetTheme.Orbit);
        config.Info.Enabled = true;
        return config;
    }

    public static void Save(AppConfig config) => Save(config, ConfigPath);

    public static void Save(AppConfig config, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Whether PrismOS has run on this machine before: its log survives a clean exit,
    /// which deletes original-wallpaper.txt. Check it before this run's Log.Init writes the log.</summary>
    public static bool RanBefore(string appDataDirectory) =>
        File.Exists(Path.Combine(appDataDirectory, "prismos.log"))
        || File.Exists(Path.Combine(appDataDirectory, "original-wallpaper.txt"));

    /// <summary>The start-up load. A fresh install is saved straight away, so its starting style
    /// sticks: otherwise its own log would make the second start look like an upgrade and turn it
    /// back to Classic.</summary>
    public static AppConfig LoadAtStartup(string configPath, bool ranBefore, out bool unreadable)
    {
        var config = Load(configPath, out unreadable, ranBefore);
        if (config.IsFreshInstall) Save(config, configPath);
        return config;
    }
}
