using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateDeletionService
{
    /// <summary>
    /// Deletes (or quarantines) all selected non-keeper members of <paramref name="group"/>.
    /// Recomputes live method-specific signatures, locks the keeper against
    /// replacement, and validates each selected file snapshot immediately before
    /// deletion. Files that changed since analysis are skipped (not deleted).
    /// Returns total bytes processed; <see cref="DeleteSelectedWithResultAsync"/>
    /// also says why each skipped file was left in place. Recycle Bin and quarantine moves do not
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

/// <summary>
/// Why a selected duplicate was left in place. Each value has a
/// <c>Safety_Duplicates_NotDeleted_&lt;value&gt;</c> string in every UI language.
/// </summary>
public enum DuplicateDeletionSkipReason
{
    /// <summary>The scan recorded no stable file identity, so a replaced file could not be told apart.</summary>
    NoStableIdentity,

    /// <summary>The file changed, moved, was replaced or disappeared since the analysis.</summary>
    ChangedSinceAnalysis,

    /// <summary>The file could not be read again to confirm it is still a duplicate.</summary>
    CouldNotVerify,

    /// <summary>The file was read again and no longer matches the file being kept.</summary>
    NoLongerDuplicate,

    /// <summary>Recycle Bin mode: Windows could only have deleted it permanently, so it was left alone.</summary>
    RecycleBinUnavailable,

    /// <summary>Windows refused the deletion (locked, access denied, …).</summary>
    DeleteFailed,

    /// <summary>The file being kept could not be verified, so nothing in its group was deleted.</summary>
    KeeperNotVerified,
}

/// <summary>A selected duplicate that was not deleted, and why.</summary>
/// <param name="Detail">Technical detail from Windows or the verifier, if any. English by policy.</param>
public sealed record DuplicateDeletionSkip(
    long MemberId,
    string Path,
    DuplicateDeletionSkipReason Reason,
    string? Detail = null);

/// <param name="Skipped">Every selected member that was left in place, with the reason.</param>
public sealed record DuplicateDeletionResult(
    long ProcessedBytes,
    int DeletedFileCount,
    IReadOnlyList<DuplicateDeletionWarning> Warnings,
    IReadOnlyList<DuplicateDeletionSkip> Skipped);
