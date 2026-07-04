namespace StorageMaster.Core.Models;

using StorageMaster.Core.Interfaces;

public enum DuplicateOperationKind
{
    Delete,
    Restore,
}

public enum DuplicateOperationStatus
{
    Planned,
    Completed,
    Quarantined,
    Restored,
    Failed,
}

/// <summary>
/// Crash-recovery record for duplicate cleanup and restore filesystem operations.
/// Intent is written before the filesystem is touched; outcome is written after.
/// </summary>
public sealed record DuplicateOperationJournalEntry
{
    public long Id { get; init; }
    public required Guid OperationId { get; init; }
    public required DuplicateOperationKind Kind { get; init; }
    public required DuplicateOperationStatus Status { get; init; }
    public required long RunId { get; init; }
    public long? GroupId { get; init; }
    public long? MemberId { get; init; }
    public long? QuarantineId { get; init; }
    public required DeletionMethod Method { get; init; }
    public required string SourcePath { get; init; }
    public string? SourceIdentity { get; init; }
    public string? DestinationPath { get; init; }
    public long SourceSizeBytes { get; init; }
    public DateTime? SourceModifiedUtc { get; init; }
    public required DateTime PlannedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public long? BytesFreed { get; init; }
    public string? ErrorMessage { get; init; }
    public string? MetadataJson { get; init; }
}
