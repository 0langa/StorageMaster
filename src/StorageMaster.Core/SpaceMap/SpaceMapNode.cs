using StorageMaster.Core.Models;

namespace StorageMaster.Core.SpaceMap;

public sealed record SpaceMapNode
{
    public required long Id { get; init; }
    public required long SessionId { get; init; }
    public required string FullPath { get; init; }
    public required string DisplayName { get; init; }
    public required SpaceMapNodeKind Kind { get; init; }
    public required long SizeBytes { get; init; }
    public required long ParentSizeBytes { get; init; }
    public required int FileCount { get; init; }
    public required int FolderCount { get; init; }
    public DateTime? ModifiedUtc { get; init; }
    public FileTypeCategory Category { get; init; } = FileTypeCategory.Unknown;
    public bool IsReparsePoint { get; init; }

    public double PercentOfParent =>
        ParentSizeBytes <= 0 ? 0d : Math.Clamp((double)SizeBytes / ParentSizeBytes * 100d, 0d, 100d);
}
