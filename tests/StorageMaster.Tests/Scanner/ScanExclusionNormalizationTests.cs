using FluentAssertions;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Tests.Scanner;

/// <summary>
/// The scanners now compare pre-normalised operands on their hot paths instead of
/// re-running Path.GetFullPath over both sides for every record. These tests pin
/// the normalised comparison to the behaviour of the normalising one, so the
/// cheaper path can never silently start matching (or missing) different paths.
/// </summary>
public sealed class ScanExclusionNormalizationTests
{
    [Theory]
    [InlineData(@"C:\Windows\Installer", @"C:\Windows\Installer", true)]
    [InlineData(@"C:\Windows\Installer\cache", @"C:\Windows\Installer", true)]
    [InlineData(@"C:\Windows\Installer\cache\deep\file.bin", @"C:\Windows\Installer", true)]
    [InlineData(@"C:\Windows\InstallerBackup", @"C:\Windows\Installer", false)]
    [InlineData(@"c:\windows\installer\cache", @"C:\Windows\Installer", true)]
    [InlineData(@"C:\Windows", @"C:\Windows\Installer", false)]
    [InlineData(@"C:\Windows\System32", @"C:\", true)]
    [InlineData(@"\\server\share\folder\file.bin", @"\\server\share\", true)]
    [InlineData(@"\\server\share-backup\file.bin", @"\\server\share\", false)]
    public void IsNormalizedPathEqualOrUnder_MatchesTheNormalizingComparison(
        string candidate,
        string ancestor,
        bool expected)
    {
        var normalizedCandidate = ScanOptionValidator.NormalizeDirectoryPath(candidate);
        var normalizedAncestor = ScanOptionValidator.NormalizeDirectoryPath(ancestor);

        ScanOptionValidator.IsNormalizedPathEqualOrUnder(normalizedCandidate, normalizedAncestor)
            .Should().Be(expected);

        // The normalising entry point must keep agreeing with it.
        ScanOptionValidator.IsPathEqualOrUnder(candidate, ancestor).Should().Be(expected);
    }

    [Fact]
    public void IsNormalizedPathExcluded_AgreesWithTheNormalizingExclusionTest()
    {
        var exclusions = ScanOptionValidator.NormalizeExcludedPaths(
            [@"C:\Windows\WinSxS", @"C:\Windows\Installer"]);

        string[] candidates =
        [
            @"C:\Windows\WinSxS\amd64_something",
            @"C:\Windows\Installer",
            @"C:\Windows\InstallerBackup\payload.msi",
            @"C:\Users\someone\Documents\report.docx",
        ];

        foreach (var candidate in candidates)
        {
            var normalized = ScanOptionValidator.NormalizeDirectoryPath(candidate);

            ScanOptionValidator.IsNormalizedPathExcluded(normalized, exclusions)
                .Should().Be(
                    ScanOptionValidator.IsExcluded(candidate, exclusions),
                    "the cheap comparison must select exactly the same paths for {0}",
                    candidate);
        }
    }

    [Fact]
    public void IsNormalizedPathExcluded_EmptyList_ExcludesNothing() =>
        ScanOptionValidator.IsNormalizedPathExcluded(@"C:\Windows\Installer", [])
            .Should().BeFalse();
}
