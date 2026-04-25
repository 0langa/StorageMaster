using FluentAssertions;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Tests.Scanner;

public sealed class FolderSizeAggregatorTests
{
    [Fact]
    public void Compute_EmptyInput_ReturnsEmpty()
    {
        var result = FolderSizeAggregator.Compute([]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Compute_SingleFolder_TotalEqualsDirectSize()
    {
        var folders = new[] { MakeFolder(@"C:\root", directBytes: 1000) };
        var result = FolderSizeAggregator.Compute(folders);

        result[@"C:\root"].Should().Be(1000);
    }

    [Fact]
    public void Compute_ParentAndChild_ParentTotalIncludesChildDirect()
    {
        var folders = new[]
        {
            MakeFolder(@"C:\root",         directBytes: 500),
            MakeFolder(@"C:\root\child",   directBytes: 1500),
        };

        var result = FolderSizeAggregator.Compute(folders);

        result[@"C:\root"].Should().Be(2000, "parent should include child's direct bytes");
        result[@"C:\root\child"].Should().Be(1500);
    }

    [Fact]
    public void Compute_DeepNesting_AllAncestorsAccumulate()
    {
        var folders = new[]
        {
            MakeFolder(@"C:\a",         directBytes: 100),
            MakeFolder(@"C:\a\b",       directBytes: 200),
            MakeFolder(@"C:\a\b\c",     directBytes: 300),
        };

        var result = FolderSizeAggregator.Compute(folders);

        result[@"C:\a\b\c"].Should().Be(300);
        result[@"C:\a\b"].Should().Be(500,  "b has its 200 plus c's 300");
        result[@"C:\a"].Should().Be(600,    "a has its 100 plus b's total 500");
    }

    [Fact]
    public void Compute_Siblings_ParentReceivesBothSiblingsTotal()
    {
        var folders = new[]
        {
            MakeFolder(@"C:\root",          directBytes: 100),
            MakeFolder(@"C:\root\alpha",    directBytes: 400),
            MakeFolder(@"C:\root\beta",     directBytes: 600),
        };

        var result = FolderSizeAggregator.Compute(folders);

        result[@"C:\root"].Should().Be(1100, "root = 100 + 400 + 600");
    }

    [Fact]
    public void Compute_ChildWithNoParentInList_DoesNotCrash()
    {
        var folders = new[] { MakeFolder(@"C:\orphan\child", directBytes: 999) };

        var act = () => FolderSizeAggregator.Compute(folders);
        act.Should().NotThrow();
    }

    [Fact]
    public void Compute_MultipleRoots_IndependentAccumulation()
    {
        var folders = new[]
        {
            MakeFolder(@"C:\rootA",         directBytes: 1000),
            MakeFolder(@"C:\rootA\child",   directBytes: 500),
            MakeFolder(@"D:\rootB",         directBytes: 2000),
            MakeFolder(@"D:\rootB\child",   directBytes: 800),
        };

        var result = FolderSizeAggregator.Compute(folders);

        result[@"C:\rootA"].Should().Be(1500);
        result[@"D:\rootB"].Should().Be(2800);
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
