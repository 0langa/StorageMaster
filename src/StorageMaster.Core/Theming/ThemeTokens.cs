namespace StorageMaster.Core.Theming;

/// <summary>Which of the two base surfaces a palette describes.</summary>
public enum ThemeMode
{
    Dark,
    Light,
}

/// <summary>
/// How the app picks a <see cref="ThemeMode"/>. Stored in settings.
/// </summary>
public enum ThemePreference
{
    System,
    Dark,
    Light,
}

/// <summary>
/// The neutral, accent-independent half of a theme: surfaces, strokes and text.
/// <para>
/// Accents deliberately cannot override these. A drive-health warning must read
/// the same whichever accent is selected, and keeping the neutral base fixed is
/// what lets a new accent be added without re-checking every screen.
/// </para>
/// </summary>
public sealed record NeutralPalette
{
    public required Rgb SurfaceBase { get; init; }
    public required Rgb SurfaceRaised { get; init; }
    public required Rgb SurfaceOverlay { get; init; }
    public required Rgb SurfaceSunken { get; init; }
    public required Rgb StrokeSubtle { get; init; }
    public required Rgb StrokeStrong { get; init; }
    public required Rgb TextPrimary { get; init; }
    public required Rgb TextSecondary { get; init; }
    public required Rgb TextMuted { get; init; }
}

/// <summary>
/// Status colours. Accent-independent for the same reason as
/// <see cref="NeutralPalette"/>: severity carries meaning, not branding.
/// <para>
/// Each severity has a fill used for bars, dots and gauges, and a separate text
/// variant. A colour dense enough to read as body text is usually too dark to
/// work as a fill, so the two are tuned independently.
/// </para>
/// </summary>
public sealed record SeverityPalette
{
    public required Rgb HealthyFill { get; init; }
    public required Rgb HealthyText { get; init; }
    public required Rgb WatchFill { get; init; }
    public required Rgb WatchText { get; init; }
    public required Rgb WarningFill { get; init; }
    public required Rgb WarningText { get; init; }
    public required Rgb CriticalFill { get; init; }
    public required Rgb CriticalText { get; init; }
    public required Rgb UnknownFill { get; init; }
    public required Rgb UnknownText { get; init; }
}

/// <summary>
/// One accent's colours for one <see cref="ThemeMode"/>.
/// </summary>
public sealed record AccentRamp
{
    /// <summary>Fill for primary buttons, selection and progress.</summary>
    public required Rgb Fill { get; init; }

    /// <summary>Hover state of <see cref="Fill"/>.</summary>
    public required Rgb FillHover { get; init; }

    /// <summary>Pressed state of <see cref="Fill"/>.</summary>
    public required Rgb FillPressed { get; init; }

    /// <summary>Text or icon drawn on top of <see cref="Fill"/>.</summary>
    public required Rgb OnFill { get; init; }

    /// <summary>The accent used as text or a thin line directly on a surface.</summary>
    public required Rgb OnSurface { get; init; }
}

/// <summary>
/// A selectable accent, with one ramp per <see cref="ThemeMode"/>.
/// Adding an accent means adding one of these to <see cref="ThemeCatalog"/>;
/// nothing else in the app needs to change.
/// </summary>
public sealed record ThemeAccent
{
    /// <summary>Stable identifier persisted in settings. Never localise this.</summary>
    public required string Id { get; init; }

    /// <summary>Resource key for the display name, resolved by the UI layer.</summary>
    public required string DisplayNameKey { get; init; }

    public required AccentRamp Dark { get; init; }
    public required AccentRamp Light { get; init; }

    public AccentRamp For(ThemeMode mode) => mode == ThemeMode.Dark ? Dark : Light;
}
