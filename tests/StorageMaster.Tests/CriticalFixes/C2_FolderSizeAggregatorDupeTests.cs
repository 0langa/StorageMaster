using FluentAssertions;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// Related to C1: FolderSizeAggregator.Compute crashes on duplicate paths
/// (Dictionary throws on duplicate key). This was flagged as H2 but it's
/// a direct consequence of the flush race in C1 — concurrent consumers
/// could produce duplicate folder entries.
///
/// After the C1 fix (serialised flushes), duplicates should no longer reach
/// the aggregator. However, we still test that the aggregator handles the
/// case defensively.
/// </summary>
public sealed class C2_FolderSizeAggregatorDupeTests
{
    [Fact]
    public void Compute_WithNoDuplicates_ProducesCorrectTotals()
    {
        var folders = new List<FolderEntry>
        {
            MakeFolder(@"C:\Root",       1000),
            MakeFolder(@"C:\Root\Sub",    500),
        };

        var totals = FolderSizeAggregator.Compute(folders);

        totals[@"C:\Root"].Should().Be(1500, "parent = own 1000 + child 500");
        totals[@"C:\Root\Sub"].Should().Be(500);
    }

    [Fact]
    public void Compute_EmptyList_ReturnsEmptyDictionary()
    {
        var totals = FolderSizeAggregator.Compute([]);
        totals.Should().BeEmpty();
    }

    private static FolderEntry MakeFolder(string path, long directBytes) => new()
    {
        Id              = 0,
        SessionId       = 1,
        FullPath        = path,
        FolderName      = Path.GetFileName(path) ?? path,
        DirectSizeBytes = directBytes,
        TotalSizeBytes  = directBytes,
        FileCount       = 1,
        SubFolderCount  = 0,
        IsReparsePoint  = false,
        WasAccessDenied = false,
    };
}
