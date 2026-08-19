using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using StorageMaster.Core.Localization;

namespace StorageMaster.Tests.Localization;

/// <summary>
/// Structural guarantees for the resource files.
/// <para>
/// These cannot judge whether a translation is <em>good</em> — a human reading
/// the running app does that. They catch the failures that make an app look
/// machine-translated regardless of wording quality: keys that exist in one
/// language and not another, placeholders that were renumbered, and strings left
/// in English because someone forgot them.
/// </para>
/// </summary>
public sealed class LocalizationTests
{
    private const string SourceLanguage = "en-US";
    private static readonly string[] TargetLanguages = ["de-DE", "es-ES"];

    /// <summary>
    /// Keys whose value is legitimately identical to English, per language.
    /// <para>
    /// Kept per language rather than as one shared list because the exemptions
    /// genuinely differ: German writes "Videos", Spanish writes "Vídeos". A shared
    /// list would quietly stop checking Spanish for a reason that only applies to
    /// German.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, HashSet<string>> IdenticalByDesign =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["de-DE"] = new(StringComparer.Ordinal)
            {
                "Nav_Dashboard",                            // used as-is in German Windows
                "Duplicates_Category_Audio",                // "Audio" is the German word
                "Duplicates_Category_Videos",               // as is "Videos"
                "SpaceMap_Legend_Videos",
                "Safety_Cleanup_Report_Column_Status",      // "Status" is the German word
                "Settings_About_VersionLabel",              // "Version " is the German word
                "Workspace_Tab_Delta",                      // technical term, kept in German
                "Enum_UiLanguage_English",                  // language names stay in their own
                "Enum_UiLanguage_German",                   // language in every locale, as Windows
                "Enum_UiLanguage_Spanish",                  // does, so users can find theirs
                "Duplicates_CustomExtensions_Placeholder",  // a list of file extensions
            },
            ["es-ES"] = new(StringComparer.Ordinal)
            {
                "Nav_Dashboard",
                "Duplicates_Category_Audio",                // "Audio" is the Spanish word
                "Health_Metric_Total",                      // as is "Total"
                "Enum_UiLanguage_English",                  // language names stay in their own
                "Enum_UiLanguage_German",                   // language in every locale, as Windows
                "Enum_UiLanguage_Spanish",                  // does, so users can find theirs
                "Duplicates_CustomExtensions_Placeholder",  // a list of file extensions
            },
        };

    [Fact]
    public void EveryLanguageDefinesTheSameKeys()
    {
        var source = ReadResources(SourceLanguage);
        source.Should().NotBeEmpty("the English resource file is the source of truth");

        foreach (var language in TargetLanguages)
        {
            var target = ReadResources(language);

            var missing = source.Keys.Except(target.Keys).OrderBy(k => k).ToArray();
            missing.Should().BeEmpty(
                "{0} is missing {1} key(s) that English defines: {2}",
                language, missing.Length, string.Join(", ", missing.Take(10)));

            var extra = target.Keys.Except(source.Keys).OrderBy(k => k).ToArray();
            extra.Should().BeEmpty(
                "{0} defines key(s) English does not, which means a string was renamed on one side only: {1}",
                language, string.Join(", ", extra.Take(10)));
        }
    }

    [Fact]
    public void PlaceholdersMatchAcrossLanguages()
    {
        var source = ReadResources(SourceLanguage);

        foreach (var language in TargetLanguages)
        {
            var target = ReadResources(language);

            foreach (var (key, englishValue) in source)
            {
                if (!target.TryGetValue(key, out var translated))
                    continue;

                var expected = Placeholders(englishValue);
                var actual = Placeholders(translated);

                actual.Should().BeEquivalentTo(expected,
                    "'{0}' in {1} must use exactly the placeholders English uses. "
                    + "Word order may change; placeholder numbers may not. English: '{2}', {1}: '{3}'",
                    key, language, englishValue, translated);
            }
        }
    }

    [Fact]
    public void NoStringWasLeftUntranslated()
    {
        var source = ReadResources(SourceLanguage);

        foreach (var language in TargetLanguages)
        {
            var target = ReadResources(language);

            var untranslated = source
                .Where(pair => !IdenticalByDesign[language].Contains(pair.Key))
                .Where(pair => target.TryGetValue(pair.Key, out var t)
                               && string.Equals(t, pair.Value, StringComparison.Ordinal)
                               && pair.Value.Any(char.IsLetter)
                               && pair.Value.Length > 3)
                .Select(pair => pair.Key)
                .OrderBy(k => k)
                .ToArray();

            untranslated.Should().BeEmpty(
                "{0} left {1} string(s) identical to English. If that is correct — a product "
                + "name or a format like CSV — add the key to IdenticalByDesign with a reason. "
                + "Keys: {2}",
                language, untranslated.Length, string.Join(", ", untranslated.Take(10)));
        }
    }

    [Fact]
    public void NoStringIsEmpty()
    {
        foreach (var language in new[] { SourceLanguage }.Concat(TargetLanguages))
        {
            var blank = ReadResources(language)
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key)
                .ToArray();

            blank.Should().BeEmpty("{0} has empty value(s) for: {1}",
                language, string.Join(", ", blank));
        }
    }

    /// <summary>
    /// Catches a sentence written where a label belongs.
    /// <para>
    /// What counts as too long depends entirely on where the string sits. A button
    /// that grew by half is a layout bug; a status sentence that grew by half is
    /// just German. The extraction pass recorded what each string is — its
    /// <c>[kind]</c> — precisely so this check can tell those apart instead of
    /// applying one ratio to both and flagging correct translations.
    /// </para>
    /// <para>
    /// It is still only a smell test. Real overflow is found by reading the running
    /// app, which is why docs/public/LOCALIZATION.md requires that before a
    /// language ships.
    /// </para>
    /// </summary>
    [Fact]
    public void TranslationsAreNotWildlyLongerThanTheirSource()
    {
        // Kinds that sit in a control sized by its English text. Several of these
        // render with TextTrimming rather than wrapping, so growth is silently lost
        // rather than visibly wrong.
        var tightKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "button", "label", "header", "title", "placeholder", "tooltip",
        };

        // Short strings expand proportionally far more than long ones: "Cancel" to
        // "Abbrechen" is +50 %, a full sentence lands nearer +30 %.
        static double TightAllowance(int sourceLength) => sourceLength switch
        {
            < 20 => 3.0,
            < 40 => 2.2,
            _ => 1.8,
        };

        // Prose is allowed to be prose. German passive constructions and Spanish
        // impersonal forms are simply longer, and forcing them under a label's
        // ratio would mean rewriting correct translations into terse ones.
        const double ProseAllowance = 2.5;

        var source = ReadResources(SourceLanguage);
        var kinds = ReadKinds();

        foreach (var language in TargetLanguages)
        {
            var target = ReadResources(language);

            var overlong = source
                .Where(pair => pair.Value.Length >= 8)
                // Safety wording is exempt entirely. The glossary forbids making a
                // translated warning shorter or friendlier than its English
                // original, and correct German safety phrasing is genuinely long —
                // Windows itself says "Dieser Vorgang kann nicht rückgängig gemacht
                // werden." for "This cannot be undone." Trimming that to satisfy a
                // ratio would be exactly the wrong trade.
                .Where(pair => !pair.Key.StartsWith("Safety_", StringComparison.Ordinal))
                .Where(pair => !string.Equals(Kind(kinds, pair.Key), "safety", StringComparison.Ordinal))
                .Where(pair =>
                {
                    if (!target.TryGetValue(pair.Key, out var translated))
                        return false;

                    var allowance = tightKinds.Contains(Kind(kinds, pair.Key))
                        ? TightAllowance(pair.Value.Length)
                        : ProseAllowance;

                    return translated.Length > pair.Value.Length * allowance;
                })
                .Select(pair => $"{pair.Key} [{Kind(kinds, pair.Key)}] ({pair.Value.Length} -> {target[pair.Key].Length})")
                .ToArray();

            overlong.Should().BeEmpty(
                "{0} has string(s) far longer than their English source. For a label or a "
                + "button that means it will be trimmed on screen; for prose it usually means "
                + "the translator added something the English does not say: {1}",
                language, string.Join(", ", overlong.Take(6)));
        }
    }

    /// <summary>
    /// Spanish questions open with an inverted mark. Missing it is the single most
    /// recognisable sign that Spanish was produced by a tool rather than written.
    /// </summary>
    [Fact]
    public void SpanishQuestionsUseInvertedOpeningPunctuation()
    {
        var offenders = ReadResources("es-ES")
            .Where(pair => pair.Value.TrimEnd().EndsWith('?') && !pair.Value.Contains('¿'))
            .Select(pair => pair.Key)
            .ToArray();

        offenders.Should().BeEmpty(
            "Spanish questions must open with '¿'. Keys: {0}", string.Join(", ", offenders));
    }

    /// <summary>
    /// The German half of the app is written in the formal register, matching
    /// Windows. A stray informal pronoun is jarring next to a system dialog.
    /// </summary>
    [Fact]
    public void GermanUsesFormalAddress()
    {
        var informal = new Regex(@"\b(du|dich|dir|dein|deine|deinen|deinem|deiner|deines)\b",
            RegexOptions.IgnoreCase);

        var offenders = ReadResources("de-DE")
            .Where(pair => informal.IsMatch(pair.Value))
            .Select(pair => $"{pair.Key}: '{pair.Value}'")
            .ToArray();

        offenders.Should().BeEmpty(
            "German uses formal 'Sie' throughout, as Windows does. Informal address found in: {0}",
            string.Join(" | ", offenders.Take(5)));
    }

    /// <summary>
    /// Safety wording is the one place a translation error can destroy data, so
    /// the distinction between recoverable and permanent removal is asserted
    /// rather than trusted.
    /// </summary>
    [Fact]
    public void SafetyStringsKeepTheRecoverableVersusPermanentDistinction()
    {
        var german = ReadResources("de-DE");
        var spanish = ReadResources("es-ES");

        german["Safety_PermanentDelete"].Should().Contain("Endgültig",
            "German must mark permanent deletion as endgültig; plain 'Löschen' is ambiguous "
            + "and is what the Recycle Bin action says");
        german["Safety_MoveToRecycleBin"].Should().Contain("Papierkorb",
            "the recoverable action must name the Recycle Bin, as Windows does");
        german["Safety_PermanentDelete"].Should().NotContain("Papierkorb",
            "permanent deletion must never mention the Recycle Bin");

        spanish["Safety_PermanentDelete"].Should().Contain("definitivamente");
        // Case-insensitively: Spanish Windows capitalises "Papelera de reciclaje"
        // as a proper noun, and the assertion is about the concept, not the casing.
        spanish["Safety_MoveToRecycleBin"].Should().ContainEquivalentOf("papelera");
        spanish["Safety_PermanentDelete"].Should().NotContainEquivalentOf("papelera");
    }

    /// <summary>
    /// The <c>[kind]</c> each English string was tagged with during extraction —
    /// button, label, status, description and so on.
    /// <para>
    /// This is authoring metadata rather than shipped content, so it is read from
    /// the resource file rather than through the catalogue, which exposes only
    /// values.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadKinds()
    {
        var root = XDocument.Load(EnglishResourcePath()).Root!;

        return root.Elements("data")
            .Where(e => e.Attribute("name") is not null)
            .Select(e => (
                Key: e.Attribute("name")!.Value,
                Match: Regex.Match(e.Element("comment")?.Value ?? string.Empty, @"^\[([a-z-]+)\]")))
            .Where(pair => pair.Match.Success)
            .ToDictionary(pair => pair.Key, pair => pair.Match.Groups[1].Value, StringComparer.Ordinal);
    }

    private static string Kind(IReadOnlyDictionary<string, string> kinds, string key)
        => kinds.TryGetValue(key, out var kind) ? kind : "status";

    private static string EnglishResourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "StorageMaster.Core", "Strings", SourceLanguage, "Resources.resw");

            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the English resource file.");
    }

    /// <summary>
    /// Reads through the catalogue rather than off disk, so these tests also fail
    /// if the .resw files stop being embedded — which would leave the shipped app
    /// showing raw resource keys while the files on disk still looked correct.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadResources(string language)
    {
        var strings = LocalizationCatalog.Strings(language);

        strings.Should().NotBeEmpty(
            "the {0} catalogue must be embedded in StorageMaster.Core; an empty one means "
            + "the EmbeddedResource glob in StorageMaster.Core.csproj no longer matches",
            language);

        return strings;
    }

    private static IReadOnlyList<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{(\d+)(?:[,:][^}]*)?\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => int.Parse(v, CultureInfo.InvariantCulture))
            .ToArray();
}
