namespace StorageMaster.Core.Models;

/// <summary>
/// Terminal states are Completed, Cancelled, Failed and Interrupted.
/// <para>
/// Interrupted means the owning process died without finishing: a crash, a kill,
/// or a power loss. It is distinct from Cancelled, which is a deliberate user
/// action with a clean partial result, and from Failed, which is an error the
/// scanner itself detected and recorded.
/// </para>
/// </summary>
public enum ScanStatus { Running, Completed, Cancelled, Failed, Interrupted }

/// <summary>Represents one scan run — the root object for all scan data.</summary>
public sealed record ScanSession
{
    public required long Id { get; init; }
    public required string RootPath { get; init; }
    public required DateTime StartedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public required ScanStatus Status { get; init; }

    /// <summary>
    /// Process that owns a <see cref="ScanStatus.Running"/> session, used to tell a
    /// genuinely live scan from one abandoned by a crash. Null on sessions written
    /// before ownership tracking existed.
    /// </summary>
    public int? OwnerProcessId { get; init; }

    /// <summary>
    /// Start time of <see cref="OwnerProcessId"/>. A process id alone is not
    /// enough: ids are recycled, so a new unrelated process could otherwise make a
    /// dead scan look alive forever.
    /// </summary>
    public DateTime? OwnerProcessStartedUtc { get; init; }
    public long TotalSizeBytes { get; init; }
    public long TotalFiles { get; init; }
    public long TotalFolders { get; init; }
    public long AccessDeniedCount { get; init; }
    public string? ErrorMessage { get; init; }

    public TimeSpan? Duration =>
        CompletedUtc.HasValue ? CompletedUtc.Value - StartedUtc : null;
}
