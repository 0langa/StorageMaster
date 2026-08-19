using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Theming;
using Windows.UI;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Applies <see cref="ThemeCatalog"/> to the live visual tree.
/// <para>
/// The brushes are created once and then <em>mutated in place</em>. A
/// <see cref="SolidColorBrush"/> is a live object, so assigning its
/// <see cref="SolidColorBrush.Color"/> updates every element already bound to it,
/// immediately and without reloading a page. Swapping merged resource
/// dictionaries instead would not do this: elements resolve a
/// <c>StaticResource</c> once at load and never look again, so an accent change
/// would only appear after navigating away and back, or after a restart.
/// </para>
/// <para>
/// This is the lever the design system needs: adding an accent to the catalogue
/// requires no change here, in XAML, or in any page.
/// </para>
/// </summary>
public sealed class ThemeService(ISettingsRepository settings)
{
    /// <summary>
    /// Resource keys are prefixed so they cannot collide with WinUI's own theme
    /// resources, which remain in use for control chrome.
    /// </summary>
    private const string Prefix = "Sm";

    private static readonly string[] BrushKeys =
    [
        "SurfaceBase", "SurfaceRaised", "SurfaceOverlay", "SurfaceSunken",
        "StrokeSubtle", "StrokeStrong",
        "TextPrimary", "TextSecondary", "TextMuted",
        "AccentFill", "AccentFillHover", "AccentFillPressed", "AccentOnFill", "AccentOnSurface",
        "SeverityHealthy", "SeverityHealthyText",
        "SeverityWatch", "SeverityWatchText",
        "SeverityWarning", "SeverityWarningText",
        "SeverityCritical", "SeverityCriticalText",
        "SeverityUnknown", "SeverityUnknownText",
    ];

    /// <summary>Number of categorical series colours published as brushes.</summary>
    public const int CategoricalSlots = 8;

    private FrameworkElement? _root;

    /// <summary>
    /// Creates the brush resources. Must run before any page that references them
    /// is loaded, because a <c>StaticResource</c> that fails to resolve is a
    /// load-time error rather than a fallback.
    /// </summary>
    public void EnsureResources()
    {
        var resources = Application.Current.Resources;

        foreach (var key in BrushKeys)
        {
            var resourceKey = Prefix + key + "Brush";
            if (!resources.ContainsKey(resourceKey))
                resources[resourceKey] = new SolidColorBrush(Colors.Transparent);
        }

        for (var i = 0; i < CategoricalSlots; i++)
        {
            var resourceKey = $"{Prefix}Categorical{i}Brush";
            if (!resources.ContainsKey(resourceKey))
                resources[resourceKey] = new SolidColorBrush(Colors.Transparent);
        }
    }

    /// <summary>
    /// Binds the service to the window root so the requested element theme can be
    /// set alongside the palette.
    /// </summary>
    public void Attach(FrameworkElement root)
    {
        _root = root;
        root.ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>Loads the persisted preference and applies it.</summary>
    public async Task ApplyFromSettingsAsync(CancellationToken ct = default)
    {
        var current = await settings.LoadAsync(ct).ConfigureAwait(true);
        Apply(current.Theme, current.AccentId);
    }

    /// <summary>
    /// Applies a preference and accent, and persists them. Persisting goes through
    /// <see cref="ISettingsRepository.UpdateAsync"/> so a concurrent write to an
    /// unrelated key is not clobbered.
    /// </summary>
    public async Task SetAsync(ThemePreference preference, string accentId, CancellationToken ct = default)
    {
        Apply(preference, accentId);

        await settings.UpdateAsync(current =>
        {
            current.Theme = preference;
            current.AccentId = accentId;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Applies a preference and accent to the visual tree only.</summary>
    public void Apply(ThemePreference preference, string? accentId)
    {
        if (_root is not null)
        {
            _root.RequestedTheme = preference switch
            {
                ThemePreference.Light => ElementTheme.Light,
                ThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        ApplyPalette(ResolveMode(preference), ThemeCatalog.ResolveAccent(accentId));
    }

    /// <summary>
    /// Resolves the effective mode. "Default" follows the system, which is read
    /// from the element's actual theme once the tree exists and falls back to dark
    /// before then — this is a dark-first app.
    /// </summary>
    private ThemeMode ResolveMode(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeMode.Light,
        ThemePreference.Dark => ThemeMode.Dark,
        _ => _root?.ActualTheme == ElementTheme.Light ? ThemeMode.Light : ThemeMode.Dark,
    };

    private void ApplyPalette(ThemeMode mode, ThemeAccent accent)
    {
        var neutral = ThemeCatalog.Neutral(mode);
        var severity = ThemeCatalog.Severity(mode);
        var ramp = accent.For(mode);
        var categorical = ThemeCatalog.Categorical(mode);

        Set("SurfaceBase", neutral.SurfaceBase);
        Set("SurfaceRaised", neutral.SurfaceRaised);
        Set("SurfaceOverlay", neutral.SurfaceOverlay);
        Set("SurfaceSunken", neutral.SurfaceSunken);
        Set("StrokeSubtle", neutral.StrokeSubtle);
        Set("StrokeStrong", neutral.StrokeStrong);
        Set("TextPrimary", neutral.TextPrimary);
        Set("TextSecondary", neutral.TextSecondary);
        Set("TextMuted", neutral.TextMuted);

        Set("AccentFill", ramp.Fill);
        Set("AccentFillHover", ramp.FillHover);
        Set("AccentFillPressed", ramp.FillPressed);
        Set("AccentOnFill", ramp.OnFill);
        Set("AccentOnSurface", ramp.OnSurface);

        // Severity is published under BOTH the prefixed runtime keys and the
        // unprefixed keys declared in Styles/Severity.xaml. The unprefixed brushes
        // must exist as real objects at parse time — an alias would have to resolve
        // while App.xaml loads, before this palette exists — so the dictionary
        // declares them and this drives their colour.
        SetSeverity("SeverityHealthy", severity.HealthyFill);
        SetSeverity("SeverityHealthyText", severity.HealthyText);
        SetSeverity("SeverityWatch", severity.WatchFill);
        SetSeverity("SeverityWatchText", severity.WatchText);
        SetSeverity("SeverityWarning", severity.WarningFill);
        SetSeverity("SeverityWarningText", severity.WarningText);
        SetSeverity("SeverityCritical", severity.CriticalFill);
        SetSeverity("SeverityCriticalText", severity.CriticalText);
        SetSeverity("SeverityUnknown", severity.UnknownFill);
        SetSeverity("SeverityUnknownText", severity.UnknownText);

        for (var i = 0; i < CategoricalSlots; i++)
            SetKey($"{Prefix}Categorical{i}Brush", categorical[i % categorical.Count]);

        ApplyBridgedSystemKeys();
    }

    /// <summary>
    /// WinUI control templates read their own brush keys, not ours. Rather than
    /// re-template every control, Styles/Palette.xaml declares brushes under those
    /// documented lightweight-styling keys and this table drives them from the
    /// corresponding palette entry, so the whole control set follows the selected
    /// theme and accent.
    /// </summary>
    private static readonly Dictionary<string, string> BridgedSystemKeys = new()
    {
        ["CardBackgroundFillColorDefaultBrush"] = "SmSurfaceRaisedBrush",
        ["CardBackgroundFillColorSecondaryBrush"] = "SmSurfaceSunkenBrush",
        ["CardStrokeColorDefaultBrush"] = "SmStrokeSubtleBrush",
        ["CardStrokeColorDefaultSolidBrush"] = "SmStrokeSubtleBrush",
        ["AccentFillColorDefaultBrush"] = "SmAccentFillBrush",
        ["AccentFillColorSecondaryBrush"] = "SmAccentFillHoverBrush",
        ["AccentFillColorTertiaryBrush"] = "SmAccentFillPressedBrush",
        ["TextOnAccentFillColorPrimaryBrush"] = "SmAccentOnFillBrush",
        ["TextOnAccentFillColorSecondaryBrush"] = "SmAccentOnFillBrush",
        ["AccentTextFillColorPrimaryBrush"] = "SmAccentOnSurfaceBrush",
        ["AccentTextFillColorSecondaryBrush"] = "SmAccentOnSurfaceBrush",
        ["AccentTextFillColorTertiaryBrush"] = "SmAccentOnSurfaceBrush",
        ["AccentButtonBackground"] = "SmAccentFillBrush",
        ["AccentButtonBackgroundPointerOver"] = "SmAccentFillHoverBrush",
        ["AccentButtonBackgroundPressed"] = "SmAccentFillPressedBrush",
        ["AccentButtonForeground"] = "SmAccentOnFillBrush",
        ["AccentButtonForegroundPointerOver"] = "SmAccentOnFillBrush",
        ["AccentButtonForegroundPressed"] = "SmAccentOnFillBrush",
        ["SystemFillColorSuccessBrush"] = "SmSeverityHealthyTextBrush",
        ["SystemFillColorCautionBrush"] = "SmSeverityWarningTextBrush",
        ["SystemFillColorCriticalBrush"] = "SmSeverityCriticalTextBrush",
        ["SystemFillColorAttentionBrush"] = "SmSeverityWatchTextBrush",
        ["NavigationViewDefaultPaneBackground"] = "SmSurfaceSunkenBrush",
        ["NavigationViewExpandedPaneBackground"] = "SmSurfaceSunkenBrush",
        ["NavigationViewContentBackground"] = "SmSurfaceBaseBrush",
        ["SolidBackgroundFillColorBaseBrush"] = "SmSurfaceBaseBrush",
    };

    private static void ApplyBridgedSystemKeys()
    {
        foreach (var (systemKey, paletteKey) in BridgedSystemKeys)
        {
            if (Application.Current.Resources.TryGetValue(paletteKey, out var source)
                && source is SolidColorBrush from
                && Application.Current.Resources.TryGetValue(systemKey, out var target)
                && target is SolidColorBrush to)
            {
                to.Color = from.Color;
            }
        }
    }

    private static void Set(string name, Rgb color) => SetKey(Prefix + name + "Brush", color);

    private static void SetSeverity(string name, Rgb color)
    {
        SetKey(Prefix + name + "Brush", color);
        SetKey(name + "Brush", color);
    }

    /// <summary>
    /// Assigns a colour to every instance of a palette brush.
    /// <para>
    /// There is deliberately more than one. A merged dictionary cannot see its
    /// siblings while it is parsed, so each style dictionary that references the
    /// palette merges <c>PaletteBrushes.xaml</c> itself — and merging the same
    /// source in several places produces a separate brush object per merge, not a
    /// shared one. Colouring only the copy reachable from
    /// <c>Application.Current.Resources</c> left every card and gauge painted with
    /// a still-transparent brush while the accent, resolved elsewhere, looked
    /// correct.
    /// </para>
    /// <para>
    /// Walking the whole dictionary tree is cheap — it runs on a theme or accent
    /// change, over a few dozen dictionaries — and it keeps the fix independent of
    /// how many places end up merging the palette.
    /// </para>
    /// </summary>
    private static void SetKey(string resourceKey, Rgb color)
    {
        var value = Color.FromArgb(0xFF, color.R, color.G, color.B);
        ApplyToDictionary(Application.Current.Resources, resourceKey, value);
    }

    private static void ApplyToDictionary(ResourceDictionary dictionary, string resourceKey, Color value)
    {
        if (dictionary.TryGetValue(resourceKey, out var existing) && existing is SolidColorBrush brush)
            brush.Color = value;

        foreach (var merged in dictionary.MergedDictionaries)
            ApplyToDictionary(merged, resourceKey, value);

        foreach (var themed in dictionary.ThemeDictionaries.Values)
        {
            if (themed is ResourceDictionary themedDictionary)
                ApplyToDictionary(themedDictionary, resourceKey, value);
        }
    }

    /// <summary>
    /// Re-applies when the system flips light/dark while the app follows it.
    /// Without this the neutral surfaces would keep their previous mode's values.
    /// </summary>
    private async void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        try
        {
            await ApplyFromSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A failed re-theme must never take the app down; the previous palette
            // stays applied and remains readable.
        }
    }
}
