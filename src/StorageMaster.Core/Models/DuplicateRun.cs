namespace StorageMaster.Core.Models;

public sealed record DuplicateRun
{
    public required long Id { get; init; }
    public required long SessionId { get; init; }
    public required DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public required DuplicateRunStatus Status { get; init; }
    public required string ConfigJson { get; init; }
    public long CandidateCount { get; init; }
    public long GroupCount { get; init; }
    public long ExactBytes { get; init; }
    public long ReclaimableBytes { get; init; }
    public long ErrorCount { get; init; }
    public string? ErrorMessage { get; init; }
}
