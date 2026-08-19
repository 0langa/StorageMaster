using System.Text.RegularExpressions;
using FluentAssertions;
using StorageMaster.Core.Localization;

namespace StorageMaster.Tests.Localization;

/// <summary>
/// Enforces the localization scope rules from docs/public/LOCALIZATION.md against
/// the source tree.
/// <para>
/// The app resolves its own strings rather than using MRT, so nothing in the
/// platform notices a key that does not exist — a typo renders as the key name on
/// screen and the build stays green. These tests are the substitute for the
/// compile-time checking <c>x:Uid</c> would have given.
/// </para>
/// </summary>
public sealed class LocalizationScopeTests
{
    /// <summary>
    /// Attributes that put text in front of a user. Deliberately narrow: widening
    /// it to every string attribute would flag style keys and tags.
    /// </summary>
    private static readonly string[] UserFacingAttributes =
    [
        "Text", "Header", "Description", "PlaceholderText", "Title",
        "ToolTipService.ToolTip", "AutomationProperties.Name",
        "AutomationProperties.HelpText",
    ];

    /// <summary>
    /// Literals that read like prose but are not shown to a user, or are proper
    /// nouns that stay identical in every language. Each entry needs a reason.
    /// </summary>
    private static readonly HashSet<string> AllowedLiterals = new(StringComparer.Ordinal)
    {
        "StorageMaster",   // product name
        "Segoe Fluent Icons", "Segoe MDL2 Assets",   // font family names

        // Placeholder text showing an example of a value the user types verbatim:
        // a page tag, a review-mode enum value, and cleanup rule ids. These are
        // matched against identifiers in code, so a translated example would show
        // the user a value the app then rejects.
        "Dashboard",
        "Exact",
        "TempFiles,CacheFolders,BrowserCache",
    };

    [Fact]
    public void EveryKeyUsedInSourceExistsInTheCatalogue()
    {
        var english = LocalizationCatalog.Strings(LocalizationCatalog.English);
        var missing = new List<string>();

        foreach (var file in SourceFiles("*.xaml").Concat(SourceFiles("*.cs")))
        {
            var text = File.ReadAllText(file);

            // Two call shapes: the XAML markup extension, whose namespace prefix is
            // chosen per file, and the C# facade. Matching the prefix loosely
            // matters — an earlier version anchored on the literal "Loc" before the
            // colon and so silently checked no XAML key at all.
            foreach (Match match in Regex.Matches(
                         text,
                         @"(?:\w+:Loc\s+Key=|Loc(?:alizationCatalog)?\.(?:Get|Format)\("")([A-Za-z0-9_]+)"))
            {
                var key = match.Groups[1].Value;
                if (!english.ContainsKey(key))
                    missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        missing.Should().BeEmpty(
            "a key with no catalogue entry renders as its own name on screen and nothing "
            + "in the build catches it. Missing: {0}",
            string.Join(", ", missing.Distinct().Take(15)));
    }

    [Fact]
    public void NoUserFacingXamlStringIsLeftAsALiteral()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles("*.xaml"))
        {
            // Styles and templates carry text that belongs to a control's own
            // chrome rather than to a screen; those files are covered by the page
            // that uses them.
            if (file.Contains($"{Path.DirectorySeparatorChar}Styles{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var attribute in UserFacingAttributes)
                {
                    foreach (Match match in Regex.Matches(lines[i], $@"\b{Regex.Escape(attribute)}=""([^""]*)"""))
                    {
                        var value = match.Groups[1].Value;

                        if (!LooksLikeProse(value))
                            continue;

                        offenders.Add($"{Path.GetFileName(file)}:{i + 1} {attribute}=\"{value}\"");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "user-facing XAML text must come from the catalogue via {{i18n:Loc Key=...}}. "
            + "If one of these is not read by a user, add it to AllowedLiterals with a "
            + "reason. Found {0}: {1}",
            offenders.Count, string.Join(" | ", offenders.Take(15)));
    }

    [Fact]
    public void LogAndCliCallSitesStayEnglish()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles("*.cs"))
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                var isLogCall = Regex.IsMatch(line, @"\bLog(Trace|Debug|Information|Warning|Error|Critical)\s*\(");
                if (isLogCall && line.Contains("Loc.", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1} (log call)");
            }

            // CLI output is scripted and piped; a localized line breaks whoever
            // parses it, and the person reading it is usually not the end user.
            if (Path.GetFileName(file).Contains("Command", StringComparison.Ordinal)
                && File.ReadAllText(file).Contains("Loc.", StringComparison.Ordinal))
            {
                offenders.Add($"{Path.GetFileName(file)} (CLI surface)");
            }
        }

        offenders.Should().BeEmpty(
            "logs, diagnostics and CLI output stay English by policy — see "
            + "docs/public/LOCALIZATION.md. Localized text found at: {0}",
            string.Join(", ", offenders.Take(10)));
    }

    /// <summary>
    /// A string that looks like a sentence or a label rather than an identifier,
    /// a glyph, a number or a binding.
    /// </summary>
    private static bool LooksLikeProse(string value)
    {
        if (value.Length < 3 || AllowedLiterals.Contains(value))
            return false;

        // Bindings, markup extensions and resource references.
        if (value.StartsWith('{'))
            return false;

        // Needs at least one lowercase letter — "OK" and "GB" alone are not prose,
        // and neither are glyph escapes.
        if (!value.Any(char.IsLower))
            return false;

        // Icon glyphs and escapes.
        if (value.Contains("\\u", StringComparison.Ordinal) || value.StartsWith("&#x", StringComparison.Ordinal))
            return false;

        // Paths, urls and format specifiers.
        if (value.Contains("://", StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || Regex.IsMatch(value, @"^[NPFDX]\d$"))
        {
            return false;
        }

        return value.Any(char.IsLetter);
    }

    private static IEnumerable<string> SourceFiles(string pattern)
    {
        var roots = new[]
        {
            Path.Combine(RepositoryRoot, "src", "StorageMaster.UI"),
            Path.Combine(RepositoryRoot, "src", "StorageMaster.Core"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                // Generated XAML code-behind and build output are not authored source.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "StorageMaster.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
