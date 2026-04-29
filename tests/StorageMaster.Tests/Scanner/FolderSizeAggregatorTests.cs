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

    [Fact]
    public void Compute_AllZeroDirectSizes_ProducesAllZeroTotals()
    {
        var folders = new[]
        {
            MakeFolder(@"C:\root",       directBytes: 0),
            MakeFolder(@"C:\root\child", directBytes: 0),
        };

        var result = FolderSizeAggregator.Compute(folders);

        result[@"C:\root"].Should().Be(0);
        result[@"C:\root\child"].Should().Be(0);
    }

    [Fact]
    public void Compute_FourLevelsDeep_AllAncestorsAccumulate()
    {
        var folders = new[]
        {
            MakeFolder(@"C:\a",             directBytes:  10),
            MakeFolder(@"C:\a\b",           directBytes:  20),
            MakeFolder(@"C:\a\b\c",         directBytes:  30),
            MakeFolder(@"C:\a\b\c\d",       directBytes:  40),
        };

        var result = FolderSizeAggregator.Compute(folders);

        result[@"C:\a\b\c\d"].Should().Be(40);
        result[@"C:\a\b\c"].Should().Be(70,   "c=30 + d=40");
        result[@"C:\a\b"].Should().Be(90,     "b=20 + c_total=70");
        result[@"C:\a"].Should().Be(100,      "a=10 + b_total=90");
    }

    [Fact]
    public void Compute_LargeSiblingTree_SumsAllBranches()
    {
        // root has 3 children, each with 2 grand-children
        var folders = new[]
        {
            MakeFolder(@"C:\r",           directBytes:   1),
            MakeFolder(@"C:\r\a",         directBytes: 100),
            MakeFolder(@"C:\r\a\x",       directBytes: 200),
            MakeFolder(@"C:\r\a\y",       directBytes: 300),
            MakeFolder(@"C:\r\b",         directBytes: 400),
            MakeFolder(@"C:\r\b\x",       directBytes: 500),
            MakeFolder(@"C:\r\b\y",       directBytes: 600),
        };

        var result = FolderSizeAggregator.Compute(folders);

        result[@"C:\r\a"].Should().Be(600,   "a=100 + x=200 + y=300");
        result[@"C:\r\b"].Should().Be(1500,  "b=400 + x=500 + y=600");
        result[@"C:\r"].Should().Be(2101,    "r=1 + a_total=600 + b_total=1500");
    }

    [Fact]
    public void Compute_SingleFolderNoParent_TotalEqualsDirectBytes()
    {
        var folder = new[] { MakeFolder(@"D:\isolated", directBytes: 42_000) };
        var result = FolderSizeAggregator.Compute(folder);

        result[@"D:\isolated"].Should().Be(42_000);
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
