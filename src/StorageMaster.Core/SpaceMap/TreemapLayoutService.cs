namespace StorageMaster.Core.SpaceMap;

public sealed class TreemapLayoutService
{
    public IReadOnlyList<SpaceMapLayoutNode> Layout(
        IReadOnlyList<SpaceMapNode> nodes,
        double width,
        double height)
    {
        if (nodes.Count == 0 || width <= 0 || height <= 0)
            return [];

        var total = nodes.Sum(static n => Math.Max(0, n.SizeBytes));
        if (total <= 0)
            return [];

        var ordered = nodes
            .Where(static n => n.SizeBytes > 0)
            .OrderByDescending(static n => n.SizeBytes)
            .ToList();

        var results = new List<SpaceMapLayoutNode>(ordered.Count);
        Slice(ordered, total, 0, 0, width, height, results);
        return results;
    }

    private static void Slice(
        IReadOnlyList<SpaceMapNode> nodes,
        long total,
        double x,
        double y,
        double width,
        double height,
        List<SpaceMapLayoutNode> results)
    {
        if (nodes.Count == 0 || total <= 0 || width <= 0 || height <= 0)
            return;

        if (nodes.Count == 1)
        {
            results.Add(new SpaceMapLayoutNode(nodes[0], x, y, width, height));
            return;
        }

        var firstGroup = new List<SpaceMapNode>();
        long firstTotal = 0;
        var half = total / 2d;

        foreach (var node in nodes)
        {
            if (firstGroup.Count > 0 && firstTotal >= half)
                break;

            firstGroup.Add(node);
            firstTotal += node.SizeBytes;
        }

        if (firstGroup.Count == nodes.Count)
        {
            var cursor = width >= height ? x : y;
            foreach (var node in nodes)
            {
                var share = (double)node.SizeBytes / total;
                if (width >= height)
                {
                    var itemWidth = width * share;
                    results.Add(new SpaceMapLayoutNode(node, cursor, y, itemWidth, height));
                    cursor += itemWidth;
                }
                else
                {
                    var itemHeight = height * share;
                    results.Add(new SpaceMapLayoutNode(node, x, cursor, width, itemHeight));
                    cursor += itemHeight;
                }
            }
            return;
        }

        var secondGroup = nodes.Skip(firstGroup.Count).ToList();
        var secondTotal = total - firstTotal;
        var ratio = Math.Clamp((double)firstTotal / total, 0.05d, 0.95d);

        if (width >= height)
        {
            var firstWidth = width * ratio;
            Slice(firstGroup, firstTotal, x, y, firstWidth, height, results);
            Slice(secondGroup, secondTotal, x + firstWidth, y, width - firstWidth, height, results);
        }
        else
        {
            var firstHeight = height * ratio;
            Slice(firstGroup, firstTotal, x, y, width, firstHeight, results);
            Slice(secondGroup, secondTotal, x, y + firstHeight, width, height - firstHeight, results);
        }
    }
}
