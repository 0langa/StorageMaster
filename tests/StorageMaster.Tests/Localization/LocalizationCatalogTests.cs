using System.Globalization;
using FluentAssertions;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Localization;

/// <summary>
/// Behaviour of the resolver itself — that the catalogue is reachable at runtime,
/// picks the right language, and degrades predictably.
/// <para>
/// These matter more than usual here because the app does not use MRT: nothing in
/// the platform verifies that a key resolves, so this is the only thing standing
/// between a missing resource and an app full of raw key names.
/// </para>
/// </summary>
[Collection(nameof(LocalizationCatalogTests))]
[CollectionDefinition(nameof(LocalizationCatalogTests), DisableParallelization = true)]
public sealed class LocalizationCatalogTests : IDisposable
{
    private readonly string _original = LocalizationCatalog.ActiveLanguage;

    public void Dispose()
    {
        // The catalogue is process-wide state. Restoring it keeps these tests from
        // changing the language other tests observe.
        LocalizationCatalog.SetLanguage(
            _original == LocalizationCatalog.German ? UiLanguage.German
            : _original == LocalizationCatalog.Spanish ? UiLanguage.Spanish
            : UiLanguage.English);
    }

    [Fact]
    public void EveryShippedLanguageIsEmbeddedAndReadable()
    {
        foreach (var language in LocalizationCatalog.ShippedLanguages)
        {
            LocalizationCatalog.Strings(language).Should().NotBeEmpty(
                "{0} must be embedded in StorageMaster.Core and parse cleanly", language);
        }
    }

    [Fact]
    public void SelectingALanguageChangesWhatIsResolved()
    {
        LocalizationCatalog.SetLanguage(UiLanguage.English);
        var english = LocalizationCatalog.Get("Nav_Settings");

        LocalizationCatalog.SetLanguage(UiLanguage.German);
        LocalizationCatalog.Get("Nav_Settings").Should().NotBe(english)
            .And.Be("Einstellungen");

        LocalizationCatalog.SetLanguage(UiLanguage.Spanish);
        LocalizationCatalog.Get("Nav_Settings").Should().Be("Configuración");
    }

    [Fact]
    public void AnUnknownKeyRendersAsItself()
    {
        LocalizationCatalog.SetLanguage(UiLanguage.German);

        LocalizationCatalog.Get("This_Key_Does_Not_Exist")
            .Should().Be("This_Key_Does_Not_Exist",
                "a visible wrong label gets noticed and fixed; a blank one does not");
    }

    [Fact]
    public void AnEmptyKeyResolvesToAnEmptyString()
        => LocalizationCatalog.Get(string.Empty).Should().BeEmpty();

    [Fact]
    public void FormattingSurvivesAPlaceholderMismatch()
    {
        LocalizationCatalog.SetLanguage(UiLanguage.English);

        // No arguments supplied for a template that wants one: the raw template is
        // shown rather than throwing on a UI thread.
        var act = () => LocalizationCatalog.Format("Shell_LowestFreeSpace");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("de-DE", "de-DE")]
    [InlineData("de-AT", "de-DE")]
    [InlineData("de", "de-DE")]
    [InlineData("es-MX", "es-ES")]
    [InlineData("en-GB", "en-US")]
    [InlineData("fr-FR", "en-US")]
    [InlineData("ja-JP", "en-US")]
    public void SystemLanguageCollapsesOntoAShippedCatalogue(string osCulture, string expected)
        => LocalizationCatalog.FromCulture(new CultureInfo(osCulture)).Should().Be(expected);

    /// <summary>
    /// A fresh install follows Windows.
    /// <para>
    /// This was deliberately pinned to English while English was the only language
    /// the app had, and the note on the property said to move it back once real
    /// translations shipped. They have. Asserting it here so the pin cannot quietly
    /// return: with it in place, a German user installing the app sees an English
    /// interface and reasonably concludes there is no German.
    /// </para>
    /// </summary>
    [Fact]
    public void AFreshInstallFollowsTheWindowsDisplayLanguage()
        => new AppSettings().Language.Should().Be(UiLanguage.System);

    [Fact]
    public void EveryUiLanguageValueResolvesToAShippedCatalogue()
    {
        foreach (UiLanguage language in Enum.GetValues<UiLanguage>())
        {
            LocalizationCatalog.ShippedLanguages
                .Should().Contain(LocalizationCatalog.Resolve(language),
                    "UiLanguage.{0} must map to a language that has strings; adding an enum "
                    + "value without a catalogue would silently show English", language);
        }
    }
}
