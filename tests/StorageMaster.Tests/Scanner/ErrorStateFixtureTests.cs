using FluentAssertions;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.Tests.Scanner;

/// <summary>
/// The error fixture only earns its place if it genuinely fails.
/// <para>
/// A fixture that quietly succeeds is worse than none: the capture harness would
/// report "no errors produced" on a machine where the app's error handling is in
/// fact broken, and the two look identical from the outside.
/// </para>
/// </summary>
public sealed class ErrorStateFixtureTests
{
    [Fact]
    public void TheDeniedFolderCannotBeEnumerated()
    {
        using var fixture = ErrorStateFixture.Create();

        var act = () => Directory.EnumerateFileSystemEntries(fixture.DeniedFolder).ToArray();

        act.Should().Throw<UnauthorizedAccessException>(
            "the scanner records a scan error only when enumeration actually throws");
    }

    [Fact]
    public void TheReadablePartIsStillReadable()
    {
        using var fixture = ErrorStateFixture.Create();

        Directory.EnumerateFiles(fixture.Root, "*", SearchOption.TopDirectoryOnly)
            .Should().NotBeNull();

        File.Exists(fixture.CorruptImage).Should().BeTrue();
        new FileInfo(fixture.CorruptImage).Length.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The state that is reachable without administrator rights, so this is the one
    /// the harness actually depends on.
    /// </summary>
    [Fact]
    public void TheLockedDuplicateCannotBeRead()
    {
        using var fixture = ErrorStateFixture.Create();

        var act = () => File.OpenRead(fixture.LockedDuplicate);

        act.Should().Throw<IOException>(
            "duplicate detection has to read each candidate, and a file it cannot open "
            + "is what puts a real entry in the errors list");
    }

    [Fact]
    public void DisposingRemovesEverythingIncludingTheDenyRule()
    {
        string root;

        using (var fixture = ErrorStateFixture.Create())
        {
            root = fixture.Root;
            Directory.Exists(root).Should().BeTrue();
        }

        Directory.Exists(root).Should().BeFalse(
            "a fixture that cannot clean itself up leaves an unreadable folder in the user's TEMP");
    }
}
