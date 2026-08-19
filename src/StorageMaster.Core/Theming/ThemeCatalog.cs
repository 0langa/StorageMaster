namespace StorageMaster.Core.Theming;

/// <summary>
/// The complete set of theme values the app ships with.
/// <para>
/// This is deliberately plain data in Core rather than XAML in the UI project.
/// It means every accent can be contrast-checked by an ordinary unit test before
/// it ever reaches a screen, and adding an accent is a data change rather than a
/// resource-dictionary migration.
/// </para>
/// <para>
/// Design intent: a dark-first instrument panel. The neutral base is a cool,
/// desaturated slate so that size bars, gauges and severity colours read as
/// signal against it. Accents are used sparingly for state and emphasis, never
/// as decoration.
/// </para>
/// </summary>
public static class ThemeCatalog
{
    public const string DefaultAccentId = "aurora";

    /// <summary>
    /// Dark base. Surfaces step upward in luminance as they come forward, so
    /// depth reads without borders doing all the work.
    /// </summary>
    public static readonly NeutralPalette Dark = new()
    {
        SurfaceBase = Rgb.Parse("#0E1116"),
        SurfaceRaised = Rgb.Parse("#161B23"),
        SurfaceOverlay = Rgb.Parse("#1E242E"),
        SurfaceSunken = Rgb.Parse("#090B0F"),
        StrokeSubtle = Rgb.Parse("#242C37"),
        StrokeStrong = Rgb.Parse("#37424F"),
        TextPrimary = Rgb.Parse("#E9EDF3"),
        TextSecondary = Rgb.Parse("#AAB4C0"),
        TextMuted = Rgb.Parse("#84909E"),
    };

    /// <summary>
    /// Light base. Not an inversion: the raised surface is pure white and the
    /// page behind it is tinted, which keeps cards reading as cards.
    /// </summary>
    public static readonly NeutralPalette Light = new()
    {
        SurfaceBase = Rgb.Parse("#F4F6F9"),
        SurfaceRaised = Rgb.Parse("#FFFFFF"),
        SurfaceOverlay = Rgb.Parse("#FFFFFF"),
        SurfaceSunken = Rgb.Parse("#E9EDF2"),
        StrokeSubtle = Rgb.Parse("#DDE3EA"),
        StrokeStrong = Rgb.Parse("#BAC4D0"),
        TextPrimary = Rgb.Parse("#0F141A"),
        TextSecondary = Rgb.Parse("#4A5563"),
        TextMuted = Rgb.Parse("#5C6875"),
    };

    public static readonly SeverityPalette DarkSeverity = new()
    {
        HealthyFill = Rgb.Parse("#2EA043"),
        HealthyText = Rgb.Parse("#56D364"),
        WatchFill = Rgb.Parse("#3B82C4"),
        WatchText = Rgb.Parse("#6CB6FF"),
        WarningFill = Rgb.Parse("#D29922"),
        WarningText = Rgb.Parse("#E3B341"),
        CriticalFill = Rgb.Parse("#DA3633"),
        CriticalText = Rgb.Parse("#FF7B72"),
        UnknownFill = Rgb.Parse("#6E7681"),
        UnknownText = Rgb.Parse("#9BA5B0"),
    };

    public static readonly SeverityPalette LightSeverity = new()
    {
        HealthyFill = Rgb.Parse("#1A7F37"),
        HealthyText = Rgb.Parse("#116329"),
        WatchFill = Rgb.Parse("#1F6FEB"),
        WatchText = Rgb.Parse("#0A4FA8"),
        WarningFill = Rgb.Parse("#BF8700"),
        WarningText = Rgb.Parse("#7A5300"),
        CriticalFill = Rgb.Parse("#CF222E"),
        CriticalText = Rgb.Parse("#A40E1F"),
        UnknownFill = Rgb.Parse("#6E7781"),
        UnknownText = Rgb.Parse("#565E66"),
    };

    /// <summary>
    /// Categorical ramp for file-type and treemap colouring. Order is stable:
    /// changing it re-colours existing charts, so append rather than reorder.
    /// </summary>
    public static readonly IReadOnlyList<Rgb> DarkCategorical =
    [
        Rgb.Parse("#4CC2D6"),
        Rgb.Parse("#7C8CF8"),
        Rgb.Parse("#E3B341"),
        Rgb.Parse("#56D364"),
        Rgb.Parse("#FF7B72"),
        Rgb.Parse("#D2A8FF"),
        Rgb.Parse("#F0883E"),
        Rgb.Parse("#8DDB8C"),
    ];

    public static readonly IReadOnlyList<Rgb> LightCategorical =
    [
        Rgb.Parse("#0B7285"),
        Rgb.Parse("#3B44B0"),
        Rgb.Parse("#8A5A00"),
        Rgb.Parse("#1A7F37"),
        Rgb.Parse("#B32B25"),
        Rgb.Parse("#6E40C9"),
        Rgb.Parse("#A4501A"),
        Rgb.Parse("#2C6E49"),
    ];

    /// <summary>
    /// Selectable accents. Every entry is contrast-verified by
    /// <c>ThemeContrastTests</c> against both neutral bases.
    /// </summary>
    public static readonly IReadOnlyList<ThemeAccent> Accents =
    [
        new ThemeAccent
        {
            Id = "aurora",
            DisplayNameKey = "Accent_Aurora",
            Dark = new AccentRamp
            {
                Fill = Rgb.Parse("#2FA8BE"),
                FillHover = Rgb.Parse("#3ABDD4"),
                FillPressed = Rgb.Parse("#2793A7"),
                OnFill = Rgb.Parse("#04191D"),
                OnSurface = Rgb.Parse("#5AC8DC"),
            },
            Light = new AccentRamp
            {
                Fill = Rgb.Parse("#0E7C8C"),
                FillHover = Rgb.Parse("#0B6674"),
                FillPressed = Rgb.Parse("#08505B"),
                OnFill = Rgb.Parse("#FFFFFF"),
                OnSurface = Rgb.Parse("#0A5F6C"),
            },
        },
        new ThemeAccent
        {
            Id = "ember",
            DisplayNameKey = "Accent_Ember",
            Dark = new AccentRamp
            {
                Fill = Rgb.Parse("#E08238"),
                FillHover = Rgb.Parse("#EE9450"),
                FillPressed = Rgb.Parse("#CC7229"),
                OnFill = Rgb.Parse("#1C0A02"),
                OnSurface = Rgb.Parse("#F0995A"),
            },
            Light = new AccentRamp
            {
                Fill = Rgb.Parse("#A4501A"),
                FillHover = Rgb.Parse("#8A4215"),
                FillPressed = Rgb.Parse("#6E3410"),
                OnFill = Rgb.Parse("#FFFFFF"),
                OnSurface = Rgb.Parse("#8A4215"),
            },
        },
        new ThemeAccent
        {
            Id = "verdant",
            DisplayNameKey = "Accent_Verdant",
            Dark = new AccentRamp
            {
                Fill = Rgb.Parse("#35AE62"),
                FillHover = Rgb.Parse("#40C271"),
                FillPressed = Rgb.Parse("#2E9C56"),
                OnFill = Rgb.Parse("#04160C"),
                OnSurface = Rgb.Parse("#5BD183"),
            },
            Light = new AccentRamp
            {
                Fill = Rgb.Parse("#1A7F37"),
                FillHover = Rgb.Parse("#146A2D"),
                FillPressed = Rgb.Parse("#0F5323"),
                OnFill = Rgb.Parse("#FFFFFF"),
                OnSurface = Rgb.Parse("#116329"),
            },
        },
        new ThemeAccent
        {
            Id = "violet",
            DisplayNameKey = "Accent_Violet",
            Dark = new AccentRamp
            {
                Fill = Rgb.Parse("#7A4BDD"),
                FillHover = Rgb.Parse("#6C3DD0"),
                FillPressed = Rgb.Parse("#5C31BC"),
                OnFill = Rgb.Parse("#FFFFFF"),
                OnSurface = Rgb.Parse("#C4A7FF"),
            },
            Light = new AccentRamp
            {
                Fill = Rgb.Parse("#6E40C9"),
                FillHover = Rgb.Parse("#5C34A8"),
                FillPressed = Rgb.Parse("#4A2A87"),
                OnFill = Rgb.Parse("#FFFFFF"),
                OnSurface = Rgb.Parse("#5C34A8"),
            },
        },
    ];

    public static NeutralPalette Neutral(ThemeMode mode) =>
        mode == ThemeMode.Dark ? Dark : Light;

    public static SeverityPalette Severity(ThemeMode mode) =>
        mode == ThemeMode.Dark ? DarkSeverity : LightSeverity;

    public static IReadOnlyList<Rgb> Categorical(ThemeMode mode) =>
        mode == ThemeMode.Dark ? DarkCategorical : LightCategorical;

    /// <summary>
    /// Resolves a persisted accent id, falling back to the default when the id is
    /// unknown. An accent removed in a future version must not leave the app
    /// unstyled.
    /// </summary>
    public static ThemeAccent ResolveAccent(string? accentId)
    {
        if (!string.IsNullOrWhiteSpace(accentId))
        {
            foreach (var accent in Accents)
            {
                if (string.Equals(accent.Id, accentId, StringComparison.OrdinalIgnoreCase))
                    return accent;
            }
        }

        return Accents.First(a => a.Id == DefaultAccentId);
    }
}
