using StorageMaster.Core.Models;
using Windows.Globalization;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Pins the interface language.
/// <para>
/// WinUI supplies its own text for built-in controls — a ToggleSwitch renders
/// "On"/"Off", a ComboBox its placeholder, dialogs their buttons — and that text
/// follows the Windows display language rather than the app's own strings. On a
/// German Windows install the result was an English app with German switches:
/// "Deep Scan / Aus". Overriding the primary language makes both halves agree.
/// </para>
/// <para>
/// The override is process-wide and is read by the resource system when a control
/// is created, so it must be set during startup, before the first window content
/// is built. Changing it later only affects controls created afterwards, which is
/// why the Settings page tells the user a restart is needed.
/// </para>
/// </summary>
public static class LanguageService
{
    /// <summary>
    /// BCP-47 tags for the languages the app ships strings for. Kept explicit
    /// rather than derived from CultureInfo so the set cannot silently widen to
    /// languages that have no translations.
    /// </summary>
    private const string English = "en-US";
    private const string German = "de-DE";

    /// <summary>
    /// BCP-47 tag for a language, for callers that need to tag the visual tree.
    /// </summary>
    public static string TagFor(UiLanguage language) => language switch
    {
        UiLanguage.English => English,
        UiLanguage.German => German,
        _ => string.Empty,
    };

    /// <summary>
    /// Applies <paramref name="language"/> to the process. Passing
    /// <see cref="UiLanguage.System"/> clears the override so Windows decides.
    /// <para>
    /// Note that this alone is not sufficient for an unpackaged app: the override
    /// is accepted and then ignored, so built-in control text still follows
    /// Windows. Styles/Inputs.xaml sets ToggleSwitch content explicitly for that
    /// reason, and the window root is tagged with the matching language.
    /// </para>
    /// </summary>
    public static void Apply(UiLanguage language)
    {
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = language switch
            {
                UiLanguage.English => English,
                UiLanguage.German => German,
                _ => string.Empty,
            };
        }
        catch (Exception)
        {
            // An unavailable language must never prevent startup; Windows then
            // picks its own, which is the same outcome as UiLanguage.System.
        }
    }

    /// <summary>
    /// The language actually in effect, for display in Settings. Reports what the
    /// resource system resolved rather than what was requested, so a language that
    /// silently failed to apply is visible instead of being reported as active.
    /// </summary>
    public static string CurrentDisplayTag =>
        string.IsNullOrEmpty(ApplicationLanguages.PrimaryLanguageOverride)
            ? ApplicationLanguages.Languages.FirstOrDefault() ?? English
            : ApplicationLanguages.PrimaryLanguageOverride;
}
