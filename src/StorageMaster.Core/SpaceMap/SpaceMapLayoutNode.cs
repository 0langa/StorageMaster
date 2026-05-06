namespace StorageMaster.Core.SpaceMap;

public sealed record SpaceMapLayoutNode(
    SpaceMapNode Node,
    double X,
    double Y,
    double Width,
    double Height);
