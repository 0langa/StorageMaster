namespace StorageMaster.Core.SpaceMap;

public sealed record ScanDeltaSummary
{
    public required long CurrentSessionId { get; init; }
    public long? PreviousSessionId { get; init; }
    public IReadOnlyList<ScanDeltaItem> GrowingFolders { get; init; } = [];
    public IReadOnlyList<ScanDeltaItem> ShrinkingFolders { get; init; } = [];
    public IReadOnlyList<ScanDeltaItem> NewLargeFiles { get; init; } = [];
    public IReadOnlyList<ScanDeltaItem> RemovedFiles { get; init; } = [];

    public bool HasComparison => PreviousSessionId is > 0;
    public ScanDeltaItem? BiggestGrowth => GrowingFolders.FirstOrDefault();
}
