namespace PrismOS.Config;

public enum ClockAnchor
{
    TopLeft, TopCenter, TopRight,
    CenterLeft, Center, CenterRight,
    BottomLeft, BottomCenter, BottomRight,
}

public sealed class ClockConfig
{
    public bool Enabled { get; set; } = true;
    public string Monitor { get; set; } = "*";
    public ClockAnchor Anchor { get; set; } = ClockAnchor.TopCenter;
    public int MarginX { get; set; } = 48;
    public int MarginY { get; set; } = 96;

    /// <summary>Per-edge overrides. Null inherits MarginX (left/right) or MarginY (top/bottom),
    /// so an untouched config behaves exactly as it did before these existed. Only the edges the
    /// anchor actually touches are consulted.</summary>
    public int? MarginLeft { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginTop { get; set; }
    public int? MarginBottom { get; set; }
    public bool TwentyFourHour { get; set; } = false;
    public bool ShowSeconds { get; set; } = false;
    public bool ShowDate { get; set; } = true;
    /// <summary>Thin horizontal rule between time and date (Mond style).</summary>
    public bool Separator { get; set; } = true;
    public float FontSize { get; set; } = 110f;
    /// <summary>Use a light-weight face for the big time ("Segoe UI Light").</summary>
    public string FontFamily { get; set; } = "Segoe UI Light";
    /// <summary>#RRGGBB or #AARRGGBB.</summary>
    public string Color { get; set; } = "#F0FFFFFF";
    public bool Shadow { get; set; } = true;

    // ---- date styling ---------------------------------------------------------------------
    // Requested on r/coolgithubprojects by a user two days into daily use: the date could not be
    // styled at all, because CreateDateFont hardcoded "Segoe UI" at 16% of the time's size.
    // Every default below reproduces that exact rendering, so upgrading changes nothing until
    // someone actually sets one.

    /// <summary>Face for the date line. Null or empty inherits the time's <see cref="FontFamily"/>.</summary>
    public string? DateFontFamily { get; set; } = "Segoe UI";

    /// <summary>Date size as a fraction of the time's font size.</summary>
    public float DateFontScale { get; set; } = 0.16f;

    /// <summary>Floor in pixels, so the date stays legible under a small clock.</summary>
    public float DateMinFontSize { get; set; } = 11f;

    /// <summary>#RRGGBB or #AARRGGBB for the date. Null inherits the time's colour, dimmed by
    /// <see cref="DateOpacity"/>.</summary>
    public string? DateColor { get; set; }

    /// <summary>Applied to the inherited colour's alpha. Ignored when DateColor is set.</summary>
    public float DateOpacity { get; set; } = 0.80f;
}

/// <summary>Classic and Editorial place each widget on its own; the rest place the set as a group
/// at <see cref="WidgetsConfig.Position"/>.</summary>
public enum WidgetTheme { Classic, Editorial, GlassDock, Orbit, Poster, Monolith, Tiles }

/// <summary>How the widgets look as a set. Classic is v0.2.0's look, and what every config written
/// before this section existed loads as.</summary>
public sealed class WidgetsConfig
{
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A string rather than an enum: a JSON enum throws on a name it does not know, and
    /// that throw makes the whole config unreadable — one typo, or a theme from a later version,
    /// would cost every other setting in the file. Read it through <see cref="ResolveTheme"/>.</summary>
    public string Theme { get; set; } = "classic";

    /// <summary>"auto" samples the wallpaper; #RRGGBB fixes it. Classic ignores it.</summary>
    public string Accent { get; set; } = "auto";

    /// <summary>Where the group themes put the set. Null is the theme's own default.
    /// Glass dock reads only the top or bottom half.</summary>
    public ClockAnchor? Position { get; set; }

    /// <summary>How big the themed widgets are, as a percentage. Unset follows the clock's
    /// <see cref="ClockConfig.FontSize"/> against its 110 px default, which is how the themes were
    /// sized before this existed — so nothing changes size until it is set.</summary>
    public int? Size { get; set; }

    /// <summary>The themes' overall scale, before the monitor's DPI factor. 50–300 %.</summary>
    public float ScaleFor(ClockConfig clock) =>
        Size is { } percent
            ? Math.Clamp(percent, 50, 300) / 100f
            : Math.Max(clock.FontSize / 110f, 0.25f);

    /// <summary>Set once the one-time "new widget styles" notice has been shown to an upgrader.</summary>
    public bool ThemesNoticeShown { get; set; }

    /// <summary>The monitor the group themes draw on; "*" is the primary.</summary>
    public string Monitor { get; set; } = "*";

    /// <summary>Frosted-glass widgets: the wallpaper shows through the pill and the cards.
    /// On by default since the music pill rework; flip in Behaviour for weaker machines.</summary>
    public bool Blur { get; set; } = true;

    /// <summary>Lock the desktop into frame 0 before the lock screen shows, ease it back open on
    /// wake, and ramp video playback from 0.2x to 1.0x over ~650 ms. Off keeps the plain
    /// start/stop behaviour at unlock.</summary>
    public bool WakeTransition { get; set; } = true;

    /// <summary>Also apply the wallpaper to the lock screen. Windows 10/11 paint the lock screen
    /// from a static image policy key, so for a video wallpaper this uses the captured still;
    /// an image wallpaper is used as-is. Off skips the write entirely (without removing any
    /// previously-set policy image).</summary>
    public bool LockScreen { get; set; } = true;

    /// <summary>The group themes: one position and one monitor for the whole set.</summary>
    public static bool IsGroup(WidgetTheme theme) =>
        theme is WidgetTheme.GlassDock or WidgetTheme.Orbit or WidgetTheme.Poster
            or WidgetTheme.Monolith or WidgetTheme.Tiles;

    /// <summary>The name a person reads, in the tray and on the Style page.</summary>
    public static string DisplayName(WidgetTheme theme) => theme switch
    {
        WidgetTheme.GlassDock => "Glass dock",
        _ => theme.ToString(),
    };

    /// <summary>The spelling written to the file: camelCase, as the design names them
    /// (classic, editorial, glassDock…).</summary>
    public static string ConfigName(WidgetTheme theme)
    {
        var name = theme.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>Case-insensitive. An unknown name renders as Classic and is logged once.</summary>
    public WidgetTheme ResolveTheme()
    {
        // Names only: Enum.TryParse would also accept "1".
        var name = Theme?.Trim() ?? "";
        if (name.Length > 0 && char.IsLetter(name[0])
            && Enum.TryParse<WidgetTheme>(name, ignoreCase: true, out var theme))
            return theme;

        lock (Warned)
            if (Warned.Add(Theme ?? ""))
                Common.Log.Warn($"Unknown widget theme '{Theme}', using Classic");
        return WidgetTheme.Classic;
    }
}

/// <summary>The info widget: a stack of lines fed by system sources, anchored independently of
/// the clock. Disabled by default — an upgrade must render exactly as it did before, the same
/// rule the date-styling defaults follow.</summary>
public sealed class InfoConfig
{
    public bool Enabled { get; set; } = false;
    public string Monitor { get; set; } = "*";
    public ClockAnchor Anchor { get; set; } = ClockAnchor.BottomLeft;
    public int MarginX { get; set; } = 48;
    public int MarginY { get; set; } = 48;

    /// <summary>Per-edge overrides, null inheriting MarginX/MarginY, exactly as ClockConfig does.</summary>
    public int? MarginLeft { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginTop { get; set; }
    public int? MarginBottom { get; set; }

    public float FontSize { get; set; } = 22f;
    public string FontFamily { get; set; } = "Segoe UI";
    public string Color { get; set; } = "#C0FFFFFF";
    public bool Shadow { get; set; } = true;

    /// <summary>Truncation budget. Scrolling a long title would mean animating, and animating
    /// means waking up.</summary>
    public int MaxCharacters { get; set; } = 48;

    /// <summary>Ordered; the order is the display order. An unknown name is logged and skipped
    /// rather than throwing, so a config written by a later version still starts this one.</summary>
    public List<string> Sources { get; set; } = ["nowPlaying", "battery"];

    public HaloConfig Halo { get; set; } = new();
    public DiscConfig Disc { get; set; } = new();
}

/// <summary>The now-playing widget: album art, artist and title, a live audio meter and a progress
/// line — the Classic record is built from the same renderer with Rotate driving its spin.
/// Rotate is the only thing that starts a timer. Setting it false gives the whole design — art,
/// progress, the lot — as a still image, and creates no timer at all.</summary>
public sealed class DiscConfig
{
    public bool Enabled { get; set; } = true;
    public int Size { get; set; } = 112;
    public bool Rotate { get; set; } = true;
    public bool ShowProgress { get; set; } = true;

    /// <summary>How hard the live meter reacts to the audio, 0.2 (barely a whisper) to 2.5
    /// (fierce). 1.0 is neutral.</summary>
    public float MeterSensitivity { get; set; } = 1f;

    /// <summary>When a track has no artwork in the media session, look the cover up on iTunes'
    /// public search API using the artist and title. Switched off for anyone who would rather a
    /// wallpaper never touch the network.</summary>
    public bool OnlineArtwork { get; set; } = true;

    /// <summary>Its own anchor, independent of the info text and the halo — the record is its own
    /// block, not a line in a list.</summary>
    public ClockAnchor Anchor { get; set; } = ClockAnchor.BottomLeft;
    public int MarginX { get; set; } = 48;
    public int MarginY { get; set; } = 48;
    public int? MarginLeft { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginTop { get; set; }
    public int? MarginBottom { get; set; }

    /// <summary>Used for the progress ring, and for the label when a track has no artwork.</summary>
    public string AccentColor { get; set; } = "#8FB4FF";

    // Typography follows the clock's hierarchy rather than the info widget's uniform text: a
    // near-white title, and a smaller dimmed artist set in spaced capitals underneath. That is
    // the same relationship the clock has between the time and the date.
    public float TitleFontSize { get; set; } = 19f;
    public float ArtistFontSize { get; set; } = 14f;
    public bool ArtistUppercase { get; set; } = true;
    public float ArtistLetterSpacing { get; set; } = 1.4f;
    public float ArtistOpacity { get; set; } = 0.62f;
}

public enum HaloPlacement { Left, Right, Above, Below }

/// <summary>The battery halo: a ring whose arc is the charge level and whose colour steps with it.
///
/// On by default, which bends the rule that an upgrade must never silently add something to
/// someone's wallpaper. Deliberate and narrow: the info widget this attaches to shipped in the
/// same release and has no installed base to surprise, so the population that rule protects is
/// empty. Had the widget shipped a release earlier this would default to false.</summary>
public sealed class HaloConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Bigger than it was: the percentage is drawn inside the ring now, and a number in
    /// a 34 px circle is not legible.</summary>
    public int Size { get; set; } = 44;

    /// <summary>False: drawn beside the info lines and moving with them. True: its own overlay,
    /// its own anchor and its own margins, and Placement is ignored.</summary>
    public bool Detached { get; set; } = false;
    public HaloPlacement Placement { get; set; } = HaloPlacement.Left;

    public ClockAnchor Anchor { get; set; } = ClockAnchor.TopRight;
    public int MarginX { get; set; } = 48;
    public int MarginY { get; set; } = 48;
    public int? MarginLeft { get; set; }
    public int? MarginRight { get; set; }
    public int? MarginTop { get; set; }
    public int? MarginBottom { get; set; }

    /// <summary>False uses HighColor at every level, so one fixed colour is a supported choice
    /// rather than something you have to fake by setting all four the same.</summary>
    public bool ColorByLevel { get; set; } = true;

    // Red at the bottom, orange in the middle, green when healthy — the conventional battery
    // reading. Thresholds are inclusive upper bounds. The warm Ember ramp is still available as a
    // preset; it is not the default, because a gold "high" says less than a green one.
    public string LowColor { get; set; } = "#FF4D4D";
    public int LowThreshold { get; set; } = 20;
    public string MidColor { get; set; } = "#FF9A3C";
    public int MidThreshold { get; set; } = 50;
    public string HighColor { get; set; } = "#5FD98A";
    public string ChargedColor { get; set; } = "#7CE8A4";
    public string TrackColor { get; set; } = "#24FFFFFF";
}
