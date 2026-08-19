using FluentAssertions;
using StorageMaster.Core.Theming;

namespace StorageMaster.Tests.Theming;

/// <summary>
/// Readability gate for the theme system.
/// <para>
/// The accent list is expected to grow. These tests exist so a new accent cannot
/// ship unreadable: adding one to <see cref="ThemeCatalog.Accents"/> automatically
/// puts it under every check below, in both themes, with no extra test authoring.
/// </para>
/// </summary>
public sealed class ThemeContrastTests
{
    public static TheoryData<ThemeMode> Modes =>
        new() { ThemeMode.Dark, ThemeMode.Light };

    public static TheoryData<string, ThemeMode> AccentsByMode
    {
        get
        {
            var data = new TheoryData<string, ThemeMode>();
            foreach (var accent in ThemeCatalog.Accents)
            {
                data.Add(accent.Id, ThemeMode.Dark);
                data.Add(accent.Id, ThemeMode.Light);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void BodyTextIsReadableOnEverySurface(ThemeMode mode)
    {
        var neutral = ThemeCatalog.Neutral(mode);
        var surfaces = new (string Name, Rgb Color)[]
        {
            ("SurfaceBase", neutral.SurfaceBase),
            ("SurfaceRaised", neutral.SurfaceRaised),
            ("SurfaceOverlay", neutral.SurfaceOverlay),
            ("SurfaceSunken", neutral.SurfaceSunken),
        };

        foreach (var (name, surface) in surfaces)
        {
            ColorContrast.Ratio(neutral.TextPrimary, surface)
                .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                    "TextPrimary must be readable on {0} in {1}", name, mode);

            ColorContrast.Ratio(neutral.TextSecondary, surface)
                .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                    "TextSecondary must be readable on {0} in {1}", name, mode);

            ColorContrast.Ratio(neutral.TextMuted, surface)
                .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumGraphicRatio,
                    "TextMuted is de-emphasised but must still be legible on {0} in {1}", name, mode);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void SurfacesAndStrokesAreDistinguishable(ThemeMode mode)
    {
        var neutral = ThemeCatalog.Neutral(mode);

        neutral.SurfaceRaised.Should().NotBe(neutral.SurfaceBase,
            "a card must be separable from the page behind it in {0}", mode);

        ColorContrast.Ratio(neutral.StrokeStrong, neutral.SurfaceRaised)
            .Should().BeGreaterThanOrEqualTo(1.6,
                "a strong stroke must be visible against a card in {0}", mode);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void SeverityColoursCarryTheirMeaning(ThemeMode mode)
    {
        var neutral = ThemeCatalog.Neutral(mode);
        var severity = ThemeCatalog.Severity(mode);

        var texts = new (string Name, Rgb Color)[]
        {
            ("Healthy", severity.HealthyText),
            ("Watch", severity.WatchText),
            ("Warning", severity.WarningText),
            ("Critical", severity.CriticalText),
            ("Unknown", severity.UnknownText),
        };

        foreach (var (name, color) in texts)
        {
            ColorContrast.Ratio(color, neutral.SurfaceRaised)
                .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                    "{0} severity text must be readable on a card in {1}", name, mode);
        }

        var fills = new (string Name, Rgb Color)[]
        {
            ("Healthy", severity.HealthyFill),
            ("Watch", severity.WatchFill),
            ("Warning", severity.WarningFill),
            ("Critical", severity.CriticalFill),
            ("Unknown", severity.UnknownFill),
        };

        foreach (var (name, color) in fills)
        {
            ColorContrast.Ratio(color, neutral.SurfaceRaised)
                .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumGraphicRatio,
                    "{0} severity fill is a graphical object and must meet 1.4.11 on a card in {1}",
                    name, mode);
        }
    }

    [Theory]
    [MemberData(nameof(AccentsByMode))]
    public void AccentIsReadableAsTextAndAsAFill(string accentId, ThemeMode mode)
    {
        var neutral = ThemeCatalog.Neutral(mode);
        var ramp = ThemeCatalog.ResolveAccent(accentId).For(mode);

        ColorContrast.Ratio(ramp.OnSurface, neutral.SurfaceBase)
            .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                "accent '{0}' used as text must be readable on the page in {1}", accentId, mode);

        ColorContrast.Ratio(ramp.OnSurface, neutral.SurfaceRaised)
            .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                "accent '{0}' used as text must be readable on a card in {1}", accentId, mode);

        ColorContrast.Ratio(ramp.OnFill, ramp.Fill)
            .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                "label on accent '{0}' fill must be readable in {1}", accentId, mode);

        ColorContrast.Ratio(ramp.OnFill, ramp.FillHover)
            .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                "label must stay readable while hovering accent '{0}' in {1}", accentId, mode);

        ColorContrast.Ratio(ramp.OnFill, ramp.FillPressed)
            .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumTextRatio,
                "label must stay readable while pressing accent '{0}' in {1}", accentId, mode);

        ColorContrast.Ratio(ramp.Fill, neutral.SurfaceBase)
            .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumGraphicRatio,
                "accent '{0}' fill is a UI object and must be discernible on the page in {1}",
                accentId, mode);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void CategoricalRampIsUsableForCharts(ThemeMode mode)
    {
        var neutral = ThemeCatalog.Neutral(mode);
        var ramp = ThemeCatalog.Categorical(mode);

        ramp.Should().HaveCountGreaterThanOrEqualTo(6,
            "treemaps and category breakdowns need enough distinct series colours");

        ramp.Should().OnlyHaveUniqueItems();

        foreach (var color in ramp)
        {
            ColorContrast.Ratio(color, neutral.SurfaceSunken)
                .Should().BeGreaterThanOrEqualTo(ColorContrast.MinimumGraphicRatio,
                    "series colour {0} must be discernible against a chart well in {1}", color, mode);
        }
    }

    [Fact]
    public void CatalogueIsWellFormed()
    {
        ThemeCatalog.Accents.Should().HaveCountGreaterThanOrEqualTo(3,
            "the user asked to be able to choose between several accents");

        ThemeCatalog.Accents.Select(a => a.Id).Should().OnlyHaveUniqueItems();
        ThemeCatalog.Accents.Select(a => a.DisplayNameKey).Should().OnlyHaveUniqueItems();

        ThemeCatalog.Accents.Should().Contain(a => a.Id == ThemeCatalog.DefaultAccentId,
            "the default accent must exist in the catalogue");

        ThemeCatalog.ResolveAccent("does-not-exist").Id
            .Should().Be(ThemeCatalog.DefaultAccentId,
                "an accent removed in a future version must not leave the app unstyled");

        ThemeCatalog.ResolveAccent(null).Id.Should().Be(ThemeCatalog.DefaultAccentId);
        ThemeCatalog.ResolveAccent("AURORA").Id.Should().Be("aurora",
            "persisted ids must resolve case-insensitively");
    }

    [Fact]
    public void ContrastMathMatchesKnownWcagValues()
    {
        var white = Rgb.Parse("#FFFFFF");
        var black = Rgb.Parse("#000000");

        ColorContrast.Ratio(white, black).Should().BeApproximately(21.0, 0.01);
        ColorContrast.Ratio(white, white).Should().BeApproximately(1.0, 0.01);
        ColorContrast.Ratio(black, white).Should().BeApproximately(21.0, 0.01,
            "contrast is order independent");

        // #767676 on white is the canonical WCAG AA boundary case.
        ColorContrast.Ratio(Rgb.Parse("#767676"), white)
            .Should().BeApproximately(4.54, 0.05);
    }
}
