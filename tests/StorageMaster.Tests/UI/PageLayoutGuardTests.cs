using System.Text.RegularExpressions;
using FluentAssertions;

namespace StorageMaster.Tests.UI;

/// <summary>
/// Static guards over the page markup.
/// <para>
/// WinUI layout cannot be exercised headlessly, so the defects this pass fixed —
/// lists that de-virtualise, a canvas that is clipped by its own minimum width, a
/// legend painted from a different palette than the thing it labels — have no
/// behavioural test that could catch a regression. Reading the markup is a blunt
/// instrument, but each rule below corresponds to a real defect that shipped, and
/// re-introducing one is exactly the kind of change that looks harmless in review.
/// </para>
/// </summary>
public sealed class PageLayoutGuardTests
{
    /// <summary>
    /// Matches an opening ListView/TreeView element and captures its attributes.
    /// Attribute values in this markup never contain angle brackets, and the
    /// negative lookahead keeps property elements such as
    /// <c>&lt;ListView.ItemTemplate&gt;</c> out of the match.
    /// </summary>
    private static readonly Regex ListElementRegex = new(
        @"<(?<kind>ListView|TreeView)(?![.\w])(?<attributes>[^<>]*)>",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// A list measured with an infinite viewport culls nothing: its panel realises
    /// every row in one UI-thread pass. Every scrolling page in this app puts its
    /// content inside a ScrollViewer, which supplies exactly that infinite height,
    /// and one cleanup rule alone can emit a thousand suggestions. So a list has to
    /// carry its own bound — which is also what keeps it usable at 200% display
    /// scale, where the page is short and an unbounded list pushes everything below
    /// it off-screen.
    /// </summary>
    [Fact]
    public void EveryListOnAPageIsHeightBoundedSoItVirtualises()
    {
        var unbounded = new List<string>();

        foreach (var page in PageMarkupFiles())
        {
            var text = File.ReadAllText(page);
            foreach (Match match in ListElementRegex.Matches(text))
            {
                var attributes = match.Groups["attributes"].Value;
                if (attributes.Contains("MaxHeight", StringComparison.Ordinal))
                    continue;

                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                unbounded.Add($"{Path.GetFileName(page)}:{line} <{match.Groups["kind"].Value}>");
            }
        }

        unbounded.Should().BeEmpty(
            "a ListView or TreeView inside a scrolling page must declare a MaxHeight " +
            "(use the ListMaxHeight / ListMaxHeightTall tokens) or it realises every " +
            "row at once and stalls the UI thread.");
    }

    /// <summary>
    /// The treemap canvas sits in a ScrollViewer whose horizontal scrolling is
    /// disabled, inside a rounded border that clips its child. A MinWidth there does
    /// not widen anything — it lays the map out across width the user cannot see and
    /// cannot scroll to, which is what hid roughly a third of the map at this
    /// machine's default display scale.
    /// </summary>
    [Fact]
    public void SpaceMapCanvasDoesNotPinAMinimumWidth()
    {
        var text = File.ReadAllText(Path.Combine(PagesDirectory(), "SpaceMapPage.xaml"));

        var canvas = Regex.Match(text, @"<Canvas(?![.\w])[^<>]*>", RegexOptions.CultureInvariant);
        canvas.Success.Should().BeTrue("the Space Map still draws its treemap onto a Canvas");

        canvas.Value.Should().NotContain("MinWidth",
            "the canvas is clipped by its border and cannot be scrolled to horizontally, " +
            "so a minimum width renders part of the map permanently unreachable");
    }

    /// <summary>
    /// The Space Map legend and the tiles it describes must resolve the same brush
    /// resources. They used to be two independent palettes — XAML hex values for the
    /// swatches, named <c>Colors.*</c> constants in C# for the tiles — and they had
    /// drifted far enough that the legend showed videos as magenta while the map drew
    /// them red.
    /// </summary>
    [Fact]
    public void TreemapTilesAndLegendResolveTheSameFileTypeBrushes()
    {
        var control = File.ReadAllText(Path.Combine(
            SourceDirectory(), "Controls", "TreemapTileControl.cs"));
        var page = File.ReadAllText(Path.Combine(PagesDirectory(), "SpaceMapPage.xaml"));

        foreach (var category in new[] { "Folder", "Image", "Video", "Archive" })
        {
            var key = $"FileType{category}Brush";

            control.Should().Contain(key,
                "the treemap tile for {0} must be painted from the shared resource", category);
            page.Should().Contain(key,
                "the legend swatch for {0} must be painted from the shared resource", category);
        }
    }

    /// <summary>
    /// The Scan page carried two competing progress indicators: a live
    /// "Step 3 of 4 · Run scan" line driven by the view model, and a static row of
    /// 1/2/3/4 cards that never highlighted the current step. Only the live one
    /// survives.
    /// </summary>
    [Fact]
    public void ScanPageHasOnlyOneStepIndicator()
    {
        var text = File.ReadAllText(Path.Combine(PagesDirectory(), "ScanPage.xaml"));

        text.Should().Contain("ViewModel.ScanStepText",
            "the view model owns the step wayfinding");

        var literalStepNumbers = Regex.Matches(text, @"Text=""[1-4]""", RegexOptions.CultureInvariant);
        literalStepNumbers.Should().BeEmpty(
            "the inert 1/2/3/4 card row duplicated the step line above it; " +
            "wayfinding lives in ScanStepText alone");
    }

    /// <summary>
    /// The severity badge sets both halves of its contrast pair. Setting only the
    /// background and inheriting the foreground is what made it flip from readable to
    /// unreadable depending on the theme.
    /// </summary>
    [Fact]
    public void SeverityBadgeSetsBothHalvesOfItsContrastPair()
    {
        var code = File.ReadAllText(Path.Combine(
            SourceDirectory(), "Controls", "SeverityBadge.xaml.cs"));

        code.Should().Contain("TextBrush",
            "the label colour must come from the severity ramp, not from the inherited theme foreground");
        code.Should().Contain("Foreground",
            "the badge must assign its own foreground");
    }

    private static IEnumerable<string> PageMarkupFiles() =>
        Directory.EnumerateFiles(PagesDirectory(), "*.xaml").OrderBy(p => p, StringComparer.Ordinal);

    private static string PagesDirectory() => Path.Combine(SourceDirectory(), "Pages");

    private static string SourceDirectory() =>
        Path.Combine(LocateRepositoryRoot(), "src", "StorageMaster.UI");

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
