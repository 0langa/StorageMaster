using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Localization;

/// <summary>
/// The application's string catalogue.
/// <para>
/// Strings are authored as <c>.resw</c> — the standard Windows resource format,
/// so translation tooling understands them — but they are resolved by this class
/// rather than by MRT. PRI generation requires
/// <c>Microsoft.Build.Packaging.Pri.Tasks.dll</c>, which ships only with Visual
/// Studio and is not present in the .NET SDK, so <c>resources.pri</c> cannot be
/// built by <c>dotnet build</c> or in CI. Reading the same files as embedded
/// resources gives identical content with no build dependency, and has the side
/// benefit of behaving the same way in unit tests as it does in the app.
/// </para>
/// <para>
/// A consequence is that <c>x:Uid</c> is unavailable — it is resolved by MRT.
/// XAML uses the <c>Loc</c> markup extension instead.
/// </para>
/// </summary>
public static class LocalizationCatalog
{
    /// <summary>
    /// The languages the app ships strings for. Explicit rather than discovered so
    /// the set cannot silently widen to a language with no translations.
    /// </summary>
    public const string English = "en-US";

    public const string German = "de-DE";
    public const string Spanish = "es-ES";

    public static IReadOnlyList<string> ShippedLanguages { get; } = [English, German, Spanish];

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Loaded =
        new(StringComparer.OrdinalIgnoreCase);

    private static volatile string _activeLanguage = English;

    /// <summary>The language strings currently resolve in.</summary>
    public static string ActiveLanguage => _activeLanguage;

    /// <summary>
    /// Selects the language for subsequent lookups.
    /// <para>
    /// <see cref="UiLanguage.System"/> resolves against the OS UI culture, falling
    /// back to English when Windows is set to a language the app does not ship.
    /// </para>
    /// </summary>
    public static void SetLanguage(UiLanguage language) => _activeLanguage = Resolve(language);

    /// <summary>
    /// The BCP-47 tag a <see cref="UiLanguage"/> maps to, narrowed to a language
    /// the app actually ships. Regional variants collapse onto their base
    /// language, so an de-AT install gets the German catalogue rather than English.
    /// </summary>
    public static string Resolve(UiLanguage language) => language switch
    {
        UiLanguage.English => English,
        UiLanguage.German => German,
        UiLanguage.Spanish => Spanish,
        _ => FromCulture(CultureInfo.CurrentUICulture),
    };

    internal static string FromCulture(CultureInfo culture)
    {
        var twoLetter = culture.TwoLetterISOLanguageName;

        foreach (var shipped in ShippedLanguages)
        {
            if (shipped.StartsWith(twoLetter, StringComparison.OrdinalIgnoreCase))
                return shipped;
        }

        return English;
    }

    /// <summary>
    /// The string for <paramref name="key"/> in the active language.
    /// <para>
    /// A key with no entry falls back to English, then to the key itself. Showing
    /// the key is deliberate: a visibly wrong label gets noticed and fixed, while a
    /// blank one looks like an intentionally empty control. The parity tests exist
    /// so this never reaches a release.
    /// </para>
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        if (Strings(_activeLanguage).TryGetValue(key, out var value) && value.Length > 0)
            return value;

        if (!string.Equals(_activeLanguage, English, StringComparison.OrdinalIgnoreCase)
            && Strings(English).TryGetValue(key, out var english) && english.Length > 0)
        {
            return english;
        }

        return key;
    }

    /// <summary>
    /// A string with positional arguments substituted.
    /// <para>
    /// Composition uses the invariant culture on purpose. Sizes, counts and dates
    /// are formatted by their own converters, which already apply the user's
    /// culture; running the composed string through it again would double-format
    /// them.
    /// </para>
    /// </summary>
    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            // A placeholder that survived review in only one language must not take
            // a page down. The untouched template is the least-bad thing to show.
            return template;
        }
    }

    /// <summary>Every key defined for a language. Used by the parity tests.</summary>
    public static IReadOnlyDictionary<string, string> Strings(string language)
        => Loaded.GetOrAdd(language, Load);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var assembly = typeof(LocalizationCatalog).Assembly;
        var name = ResourceName(assembly, language);

        if (name is null)
            return Empty;

        using var stream = assembly.GetManifestResourceStream(name);
        return stream is null ? Empty : Parse(XDocument.Load(stream));
    }

    private static Dictionary<string, string> Empty => new(StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> Parse(XDocument document)
        => document.Root?
               .Elements("data")
               .Where(e => e.Attribute("name") is not null)
               .GroupBy(e => e.Attribute("name")!.Value, StringComparer.Ordinal)
               .ToDictionary(
                   g => g.Key,
                   g => g.First().Element("value")?.Value ?? string.Empty,
                   StringComparer.Ordinal)
           ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static string? ResourceName(Assembly assembly, string language)
    {
        // Strings\de-DE\Resources.resw becomes
        // StorageMaster.Core.Strings.de_DE.Resources.resw: MSBuild builds the
        // manifest name from the path and replaces characters that cannot appear in
        // an identifier, so the hyphen in a BCP-47 tag turns into an underscore.
        // Both spellings are accepted rather than relying on that substitution
        // staying exactly as it is.
        var candidates = new[]
        {
            $".Strings.{language}.Resources.resw",
            $".Strings.{language.Replace('-', '_')}.Resources.resw",
        };

        return Array.Find(
            assembly.GetManifestResourceNames(),
            n => candidates.Any(suffix => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }
}
