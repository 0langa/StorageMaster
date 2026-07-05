namespace StorageMaster.Core.Models;

/// <summary>
/// Tracks a file that was moved to the quarantine folder instead of being
/// permanently deleted or recycled, allowing safe restore.
/// </summary>
public sealed record QuarantinedFile
{
    public required long Id { get; init; }

    /// <summary>
    /// Duplicate group member this quarantine belongs to, or null when the
    /// file was quarantined by the generic cleanup engine (schema v9+).
    /// </summary>
    public required long? MemberId { get; init; }
    public required long RunId { get; init; }
    public required string OriginalPath { get; init; }
    public required string QuarantinePath { get; init; }
    public required DateTime QuarantinedUtc { get; init; }
    public DateTime? RestoredUtc { get; init; }
    public string? RestoredPath { get; init; }
}
