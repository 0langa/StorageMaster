using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

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
    /// Keys whose value is legitimately identical to English — product names,
    /// file formats, and terms the glossary keeps untranslated on purpose.
    /// </summary>
    private static readonly HashSet<string> IdenticalByDesign = new(StringComparer.Ordinal)
    {
        "Nav_Dashboard",
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
                .Where(pair => !IdenticalByDesign.Contains(pair.Key))
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
    /// German and Spanish run materially longer than English, and short strings
    /// expand proportionally far more than long ones — "Cancel" to "Abbrechen" is
    /// +50 %, while a full sentence lands nearer +30 %. A flat ratio therefore
    /// flags correct translations of short labels, so the allowance is tiered the
    /// way localization guidance actually describes expansion.
    /// <para>
    /// This is a smell test for a sentence written where a label belongs, not a
    /// layout check. Only reading the running app catches real overflow.
    /// </para>
    /// </summary>
    [Fact]
    public void TranslationsAreNotWildlyLongerThanTheirSource()
    {
        static double AllowedGrowth(int sourceLength) => sourceLength switch
        {
            < 20 => 3.0,
            < 40 => 2.2,
            _ => 1.8,
        };

        var source = ReadResources(SourceLanguage);

        foreach (var language in TargetLanguages)
        {
            var target = ReadResources(language);

            var overlong = source
                .Where(pair => pair.Value.Length >= 8)
                // Safety wording is exempt. The glossary forbids making a translated
                // warning shorter or friendlier than its English original, and correct
                // German safety phrasing is genuinely long — Windows itself says
                // "Dieser Vorgang kann nicht rückgängig gemacht werden." for
                // "This cannot be undone." Trimming it to satisfy a ratio would be
                // exactly the wrong trade.
                .Where(pair => !pair.Key.StartsWith("Safety_", StringComparison.Ordinal))
                .Where(pair => target.TryGetValue(pair.Key, out var t)
                               && t.Length > pair.Value.Length * AllowedGrowth(pair.Value.Length))
                .Select(pair => $"{pair.Key} ({pair.Value.Length} -> {target[pair.Key].Length})")
                .ToArray();

            overlong.Should().BeEmpty(
                "{0} has string(s) far longer than their English source, which usually means a "
                + "sentence was written where a label was intended: {1}",
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
        spanish["Safety_MoveToRecycleBin"].Should().Contain("papelera");
        spanish["Safety_PermanentDelete"].Should().NotContain("papelera");
    }

    private static IReadOnlyDictionary<string, string> ReadResources(string language)
    {
        var path = ResourcePath(language);
        File.Exists(path).Should().BeTrue("resource file for {0} must exist at {1}", language, path);

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(e => e.Attribute("name") is not null)
            .ToDictionary(
                e => e.Attribute("name")!.Value,
                e => e.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static string ResourcePath(string language)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "StorageMaster.UI", "Strings", language, "Resources.resw");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the {language} resource file.");
    }

    private static IReadOnlyList<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{(\d+)(?:[,:][^}]*)?\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => int.Parse(v, CultureInfo.InvariantCulture))
            .ToArray();
}
