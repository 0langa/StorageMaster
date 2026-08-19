using System.Text;
using FluentAssertions;

namespace StorageMaster.Tests.UI;

/// <summary>
/// Guards the page view-models against silently returning to doing long work on the
/// WinUI dispatcher.
/// <para>
/// These assertions read source rather than calling the view-models, because this test
/// assembly cannot reference <c>StorageMaster.UI</c>: it is a WinUI application project,
/// and every type in <c>Pages</c> reaches <c>Microsoft.UI.Xaml</c> through the
/// navigation, dialog and converter types it depends on. Bringing the Windows App SDK
/// into the test host to construct one view-model would cost far more than the
/// invariants are worth. What is checked here is exactly the shape that makes the
/// difference between a responsive window and a crawling one — where the work is
/// handed off — plus the deletion confirmation, which must never be refactored away.
/// </para>
/// </summary>
public sealed class PageViewModelThreadingGuardTests
{
    private static readonly string PagesDirectory = LocatePagesDirectory();

    [Fact]
    public void DuplicateDeletionIsHandedToTheThreadPool()
    {
        var body = MethodBody("DuplicatesViewModel.cs", "private async Task DeleteSelectedAsync()");

        body.Should().Contain("Task.Run(",
            "deleting a page of duplicates re-reads and SHA-256-hashes every selected file "
            + "before removing it; awaited straight from the command that work runs on the "
            + "dispatcher and the window crawls for the whole run");
        body.Should().Contain("ExecuteDeletionPlanAsync",
            "the per-group loop belongs in the offloaded helper, not inline in the command");
        body.Should().NotContain("_duplicateDeletionService.DeleteSelectedWithResultAsync",
            "calling the deletion service directly from the command body puts it back on the dispatcher");
    }

    [Fact]
    public void DuplicateDeletionStillAsksForConfirmation()
    {
        var body = MethodBody("DuplicatesViewModel.cs", "private async Task DeleteSelectedAsync()");

        body.Should().Contain("_dialogs.ConfirmAsync(",
            "no rework of this command may remove the confirmation that gates it");
        body.Should().Contain("if (!confirmed",
            "a declined confirmation must abandon the deletion");
    }

    [Fact]
    public void DuplicateReportExportIsHandedToTheThreadPool()
    {
        var body = MethodBody("DuplicatesViewModel.cs", "private async Task RunExportAsync(");

        body.Should().Contain("Task.Run(() => exportAction(",
            "a report walks the whole run with a query per group; run from the dispatcher it "
            + "competes with the Cancel Export button it is supposed to let the user press");
        body.Should().Contain("new Progress<string>(",
            "the writers run on the pool, so their status text has to be marshalled back "
            + "rather than assigned to a bound property directly");
    }

    [Fact]
    public void ScanPathValidationDoesNotTouchTheDiskDirectly()
    {
        var body = MethodBody("ScanViewModel.cs", "private void ValidateSelectedPath(string? path)");

        body.Should().NotContain("File.WriteAllText",
            "the AppData write probe must go through the cached helper; it answers a question "
            + "about this install, not about the path being validated, so repeating it on every "
            + "validation is pure UI-thread disk I/O");
        body.Should().NotContain("new DriveInfo(",
            "DriveInfo.IsReady blocks for seconds on a stale mapped or spun-down drive and must "
            + "go through the short-lived readiness cache");
    }

    [Fact]
    public void SpaceMapResizeDoesNotRelayoutOnEveryEvent()
    {
        var body = MethodBody("SpaceMapViewModel.cs", "public void ResizeLayout(double width, double height)");

        body.Should().NotContain("Relayout()",
            "SizeChanged fires repeatedly through a window drag, and every relayout makes the "
            + "page tear down and rebuild all treemap tiles; the sizes have to be coalesced first");
        body.Should().Contain("_resizeCoalescing",
            "the coalescing window is what collapses a drag into one relayout");
    }

    /// <summary>
    /// Returns the block body of a method, located by the exact text of its declaration
    /// and delimited by brace matching. The view-model sources contain no brace-escaped
    /// interpolations or character literals in these methods, so counting is reliable.
    /// </summary>
    private static string MethodBody(string fileName, string declaration)
    {
        var path = Path.Combine(PagesDirectory, fileName);
        File.Exists(path).Should().BeTrue($"{fileName} is expected under {PagesDirectory}");

        var source = File.ReadAllText(path);
        var declarationIndex = source.IndexOf(declaration, StringComparison.Ordinal);
        declarationIndex.Should().BeGreaterThanOrEqualTo(0,
            $"'{declaration}' is expected in {fileName}; if it was renamed, update this guard "
            + "rather than deleting it");

        var openIndex = source.IndexOf('{', declarationIndex);
        openIndex.Should().BeGreaterThanOrEqualTo(0, $"'{declaration}' is expected to have a block body");

        var depth = 0;
        var body = new StringBuilder();
        for (var i = openIndex; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '{')
                depth++;
            else if (c == '}')
                depth--;

            body.Append(c);
            if (depth == 0)
                return body.ToString();
        }

        throw new InvalidOperationException($"Unbalanced braces after '{declaration}' in {fileName}.");
    }

    private static string LocatePagesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StorageMaster.sln")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException("Could not locate the repository root from the test output directory.");

        return Path.Combine(directory.FullName, "src", "StorageMaster.UI", "Pages");
    }
}
