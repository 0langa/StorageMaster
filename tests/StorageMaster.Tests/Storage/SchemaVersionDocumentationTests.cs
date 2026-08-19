using System.Text.RegularExpressions;
using FluentAssertions;
using StorageMaster.Storage.Schema;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// Keeps the public documentation's schema number tied to the code.
/// <para>
/// The current-state docs sat at v12 while <see cref="DatabaseSchema.CurrentVersion"/>
/// had moved to v15, and one of them described a migration behaviour the code no
/// longer had. Prose drift like that is invisible to every other test, and it is
/// what a user reads when their own database reports a number they cannot explain.
/// </para>
/// <para>
/// Each current-state document carries a machine-readable marker
/// (<c>&lt;!-- schema-version: N --&gt;</c>) plus the same number in the prose a
/// human actually reads, so the two cannot diverge either.
/// </para>
/// </summary>
public sealed class SchemaVersionDocumentationTests
{
    private static readonly string[] CurrentStateDocuments =
    [
        "README.md",
        Path.Combine("docs", "public", "ARCHITECTURE.md"),
        Path.Combine("docs", "public", "CODEMAP.md"),
        Path.Combine("docs", "public", "DOCUMENTATION.md"),
    ];

    private static readonly Regex MarkerRegex = new(
        @"<!--\s*schema-version:\s*(?<version>\d+)\s*-->",
        RegexOptions.CultureInvariant);

    [Fact]
    public void CurrentStateDocuments_PinTheLiveSchemaVersion()
    {
        var root = LocateRepositoryRoot();
        var expected = DatabaseSchema.CurrentVersion;
        var problems = new List<string>();

        foreach (var relativePath in CurrentStateDocuments)
        {
            var fullPath = Path.Combine(root, relativePath);
            if (!File.Exists(fullPath))
            {
                problems.Add($"{relativePath}: file not found at {fullPath}.");
                continue;
            }

            var text = File.ReadAllText(fullPath);

            var markers = MarkerRegex.Matches(text);
            if (markers.Count != 1)
            {
                problems.Add(
                    $"{relativePath}: expected exactly one '<!-- schema-version: N -->' marker, found {markers.Count}.");
                continue;
            }

            var declared = int.Parse(
                markers[0].Groups["version"].Value,
                System.Globalization.CultureInfo.InvariantCulture);

            if (declared != expected)
                problems.Add($"{relativePath}: marker says v{declared}, code is at v{expected}.");

            if (!text.Contains($"schema v{expected}", StringComparison.Ordinal))
            {
                problems.Add(
                    $"{relativePath}: marker is correct but the prose never says 'schema v{expected}', " +
                    "so a reader would still see the old number.");
            }
        }

        problems.Should().BeEmpty(
            "public docs must state the schema version the code actually ships. " +
            $"Bumping DatabaseSchema.CurrentVersion to {expected} means updating the marker and the " +
            "prose in each listed document, and describing the new level in ARCHITECTURE.md's " +
            "migration list and CODEMAP.md's migration table.");
    }

    // Note: that every level actually runs is covered behaviourally by
    // CriticalFixes/AtomicMigrationTests, which migrates a real database and asserts
    // one stamped SchemaVersion row per level. This file only guards the prose.

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StorageMaster.sln")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException("Could not locate the repository root from the test output directory.");

        return directory.FullName;
    }
}
