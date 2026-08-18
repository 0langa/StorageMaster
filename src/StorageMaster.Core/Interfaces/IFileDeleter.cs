using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public enum DeletionMethod
{
    RecycleBin,
    Permanent,
    /// <summary>
    /// Move file to the app's quarantine folder
    /// (%LOCALAPPDATA%\StorageMaster\Quarantine\&lt;runId&gt;\...).
    /// The move is reversible via <see cref="IDuplicateDeletionService.RestoreFromQuarantineAsync"/>.
    /// </summary>
    Quarantine,
}

public sealed record DeletionRequest(
    string Path,
    DeletionMethod Method,
    bool DryRun,
    /// <summary>Optional run ID required when Method == Quarantine.</summary>
    long? QuarantineRunId = null,
    /// <summary>
    /// Optional live snapshot that must still match immediately before deletion.
    /// A mismatch fails closed instead of deleting a replaced/changed file.
    /// </summary>
    FileSnapshot? ExpectedSnapshot = null);

public sealed record DeletionOutcome(
    string Path,
    bool Success,
    long BytesFreed,
    string? Error = null,
    /// <summary>Set when Method == Quarantine and move succeeded.</summary>
    string? QuarantinePath = null);

/// <summary>
/// Platform abstraction for file/folder deletion.
/// All deletion paths flow through this interface so they can be logged,
/// tested, and swapped (e.g., for a mock in tests).
/// </summary>
public interface IFileDeleter
{
    /// <summary>
    /// Deletes or recycles a single path. Never throws for per-file errors —
    /// returns a failed DeletionOutcome instead.
    /// </summary>
    Task<DeletionOutcome> DeleteAsync(DeletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Batch overload — processes in parallel with bounded concurrency.</summary>
    IAsyncEnumerable<DeletionOutcome> DeleteManyAsync(
        IReadOnlyList<DeletionRequest> requests,
        CancellationToken cancellationToken = default);
}
