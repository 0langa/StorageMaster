using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateDeletionService
{
    /// <summary>
    /// Deletes (or quarantines) all selected non-keeper members of <paramref name="group"/>.
    /// Recomputes live method-specific signatures, locks the keeper against
    /// replacement, and validates each selected file snapshot immediately before
    /// deletion. Files that changed since analysis are skipped (not deleted).
    /// Returns total bytes processed. Recycle Bin and quarantine moves do not
    /// necessarily release filesystem allocation.
    /// </summary>
    Task<long> DeleteSelectedAsync(
        DuplicateGroup group,
        IReadOnlyList<DuplicateGroupMember> members,
        DeletionMethod method,
        CancellationToken ct = default);

    /// <summary>
    /// Executes the same guarded deletion workflow while returning non-fatal persistence
    /// warnings. A moved/deleted file is never reattempted merely because later catalog,
    /// member-state, or audit bookkeeping failed.
    /// </summary>
    Task<DuplicateDeletionResult> DeleteSelectedWithResultAsync(
        DuplicateGroup group,
        IReadOnlyList<DuplicateGroupMember> members,
        DeletionMethod method,
        CancellationToken ct = default);

    /// <summary>
    /// Restores a previously quarantined file back to its original path
    /// (or to <paramref name="targetPath"/> if supplied).
    /// </summary>
    Task RestoreFromQuarantineAsync(
        long quarantineId,
        string? targetPath = null,
        CancellationToken ct = default);
}

public sealed record DuplicateDeletionWarning(string Path, string Message);

public sealed record DuplicateDeletionResult(
    long ProcessedBytes,
    int DeletedFileCount,
    IReadOnlyList<DuplicateDeletionWarning> Warnings);
