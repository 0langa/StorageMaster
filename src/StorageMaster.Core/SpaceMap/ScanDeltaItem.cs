namespace StorageMaster.Core.SpaceMap;

public sealed record ScanDeltaItem
{
    public required string FullPath { get; init; }
    public required string DisplayName { get; init; }
    public required SpaceMapNodeKind Kind { get; init; }
    public required long CurrentBytes { get; init; }
    public required long PreviousBytes { get; init; }
    public long DeltaBytes => CurrentBytes - PreviousBytes;
}
