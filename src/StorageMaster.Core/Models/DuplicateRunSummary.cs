namespace StorageMaster.Core.Models;

public sealed record DuplicateRunSummary
{
    public long RunId { get; init; }
    public long GroupCount { get; init; }
    public long ExactGroupCount { get; init; }
    public long ReviewGroupCount { get; init; }
    public long ReclaimableBytes { get; init; }
    public long ErrorCount { get; init; }
}
