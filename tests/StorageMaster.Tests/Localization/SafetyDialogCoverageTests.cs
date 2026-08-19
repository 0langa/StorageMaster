using System.Text.RegularExpressions;
using FluentAssertions;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.Tests.Localization;

/// <summary>
/// Every safety confirmation the app can show must be reachable by the capture
/// harness.
/// <para>
/// The harness rebuilds these dialogs from a declared catalogue, because a real one
/// only appears while someone is deleting something and a capture run must never be
/// the thing that starts a deletion. The cost of declaring them is that a new dialog
/// would not be in the list and would go unreviewed — in exactly the wording where
/// an unreviewed translation can cost someone their files.
/// </para>
/// <para>
/// This closes that gap mechanically: it reads the confirmation call sites out of
/// the view models and fails when one names a title the catalogue does not carry.
/// </para>
/// </summary>
public sealed class SafetyDialogCoverageTests
{
    [Fact]
    public void EverySafetyConfirmationIsInTheScenarioCatalogue()
    {
        var declared = ScenarioCatalogue.SafetyDialogs
            .Select(scenario => scenario.TitleKey)
            .ToHashSet(StringComparer.Ordinal);

        var used = ConfirmationTitleKeysInSource();

        used.Should().NotBeEmpty("the call sites are found by pattern; an empty result means the pattern stopped matching rather than that the app stopped confirming");

        var missing = used.Except(declared).OrderBy(key => key).ToArray();

        missing.Should().BeEmpty(
            "a confirmation the harness cannot render is a confirmation nobody reviews in German or Spanish. "
            + "Add it to ScenarioCatalogue.SafetyDialogs. Missing: {0}",
            string.Join(", ", missing));
    }

    /// <summary>
    /// The first argument of every <c>ConfirmAsync</c> call, which is the dialog's
    /// title key.
    /// </summary>
    private static IReadOnlyCollection<string> ConfirmationTitleKeysInSource()
    {
        var pages = Path.Combine(RepositoryRoot, "src", "StorageMaster.UI", "Pages");
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(pages, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var text = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(
                         text,
                         @"ConfirmAsync\(\s*Loc\.(?:Get|Format)\(""(Safety_[A-Za-z0-9_]+)""",
                         RegexOptions.Singleline))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        return keys;
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
