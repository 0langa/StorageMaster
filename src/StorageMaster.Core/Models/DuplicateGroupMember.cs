namespace StorageMaster.Core.Models;

public sealed record DuplicateGroupMember
{
    public required long Id { get; init; }
    public required long GroupId { get; init; }
    public required long FileEntryId { get; init; }
    public required string FullPath { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required double Score { get; init; }
    public required bool IsKeeper { get; init; }
    public required bool IsSelected { get; init; }
    public required string RecommendationReason { get; init; }
    public required bool ExistsNow { get; init; }
}
