using FluentAssertions;
using StorageMaster.Core.SpaceMap;

namespace StorageMaster.Tests.Scanner;

public sealed class TreemapLayoutServiceTests
{
    [Fact]
    public void Layout_CoversRequestedBoundsWithoutNegativeRectangles()
    {
        var service = new TreemapLayoutService();
        var nodes = new[]
        {
            MakeNode("A", 60),
            MakeNode("B", 30),
            MakeNode("C", 10),
        };

        var layout = service.Layout(nodes, 100, 80);

        layout.Should().HaveCount(3);
        layout.Should().OnlyContain(static node =>
            node.X >= 0 &&
            node.Y >= 0 &&
            node.Width > 0 &&
            node.Height > 0 &&
            node.X + node.Width <= 100.0001 &&
            node.Y + node.Height <= 80.0001);
    }

    [Fact]
    public void Layout_ZeroSizedInput_ReturnsEmpty()
    {
        var service = new TreemapLayoutService();

        var layout = service.Layout([MakeNode("A", 0)], 100, 80);

        layout.Should().BeEmpty();
    }

    private static SpaceMapNode MakeNode(string name, long size) => new()
    {
        Id = 1,
        SessionId = 1,
        FullPath = $@"C:\Root\{name}",
        DisplayName = name,
        Kind = SpaceMapNodeKind.Folder,
        SizeBytes = size,
        ParentSizeBytes = 100,
        FileCount = 0,
        FolderCount = 0,
    };
}
