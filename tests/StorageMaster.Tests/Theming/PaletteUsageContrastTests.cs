using FluentAssertions;
using StorageMaster.Core.Theming;

namespace StorageMaster.Tests.Theming;

/// <summary>
/// Contrast guarantees that specific controls now depend on.
/// <para>
/// <see cref="ThemeContrastTests"/> proves the palette is internally sane. These
/// tests are narrower and more brittle on purpose: each one is the promise a real
/// control makes, so changing the palette in a way that breaks that control fails
/// here rather than on a user's screen.
/// </para>
/// </summary>
public sealed class PaletteUsageContrastTests
{
    public static TheoryData<ThemeMode> Modes =>
        new() { ThemeMode.Dark, ThemeMode.Light };

    /// <summary>
    /// <c>SeverityBadge</c> renders as an outlined chip: the label and the ring take
    /// the severity ramp's text colour and the surface behind it is whatever the
    /// host card uses. It appears on Dashboard inside a sunken row card and on Drive
    /// Health inside a raised section card, so "readable on a card" is not enough —
    /// it has to hold on every surface the app paints.
    /// </summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void SeverityLabelIsReadableOnEverySurfaceItCanLandOn(ThemeMode mode)
    {
        var neutral = ThemeCatalog.Neutral(mode);
        var severity = ThemeCatalog.Severity(mode);

        var surfaces = new (string Name, Rgb Color)[]
        {
            ("SurfaceBase", neutral.SurfaceBase),
            ("SurfaceRaised", neutral.SurfaceRaised),
            ("SurfaceOverlay", neutral.SurfaceOverlay),
            ("SurfaceSunken", neutral.SurfaceSunken),
        };

        var labels = new (string Name, Rgb Color)[]
        {
            ("Healthy", severity.HealthyText),
            ("Watch", severity.WatchText),
            ("Warning", severity.WarningText),
            ("Critical", severity.CriticalText),
            ("Unknown", severity.UnknownText),
        };

        foreach (var (surfaceName, surface) in surfaces)
        {
            foreach (var (labelName, label) in labels)
            {
                ColorContrast.Ratio(label, surface)
                    .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                        "the {0} severity badge must stay readable on {1} in {2}",
                        labelName, surfaceName, mode);
            }
        }
    }

    /// <summary>
    /// Treemap tiles are filled with a categorical colour and then labelled on top of
    /// that fill. The control cannot use the theme foreground, because the ramp is
    /// light in dark mode and dark in light mode; it picks near-black or white by
    /// relative luminance instead.
    /// <para>
    /// This test mirrors that rule against the catalogue. It is what stops someone
    /// adding a mid-luminance series colour that neither black nor white can label.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryCategoricalColourCanCarryAReadableLabel(ThemeMode mode)
    {
        // Kept in sync with TreemapTileControl.LabelBrushFor. 0.18 is the crossover
        // where black-on-fill overtakes white-on-fill for the WCAG ratio.
        const double LuminanceCrossover = 0.18;
        var onLightFill = Rgb.Parse("#0E1116");
        var onDarkFill = Rgb.Parse("#FFFFFF");

        foreach (var fill in ThemeCatalog.Categorical(mode))
        {
            var label = ColorContrast.RelativeLuminance(fill) > LuminanceCrossover
                ? onLightFill
                : onDarkFill;

            ColorContrast.Ratio(label, fill)
                .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                    "a treemap tile filled {0} must still be able to show its name and size in {1}",
                    fill, mode);
        }
    }

    /// <summary>
    /// The card system carries depth with luminance rather than with borders: a well
    /// is cut into a card, a card is raised off the page, and the one hero panel per
    /// page steps forward again. That only reads if the four surfaces stay ordered,
    /// and the order has to run the same way in both themes — light mode is not an
    /// inversion of dark mode, it is its own ladder.
    /// </summary>
    [Theory]
    [MemberData(nameof(Modes))]
    public void SurfacesFormADepthLadderInBothThemes(ThemeMode mode)
    {
        var neutral = ThemeCatalog.Neutral(mode);

        var sunken = ColorContrast.RelativeLuminance(neutral.SurfaceSunken);
        var page = ColorContrast.RelativeLuminance(neutral.SurfaceBase);
        var raised = ColorContrast.RelativeLuminance(neutral.SurfaceRaised);
        var overlay = ColorContrast.RelativeLuminance(neutral.SurfaceOverlay);

        sunken.Should().BeLessThan(page,
            "a well must sit below the page it is cut into, in {0}", mode);
        page.Should().BeLessThan(raised,
            "a card must sit above the page it is raised off, in {0}", mode);
        raised.Should().BeLessThanOrEqualTo(overlay,
            "the hero panel must not fall behind an ordinary card, in {0}", mode);
    }
}
