using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateDeletionService
{
    /// <summary>
    /// Deletes (or quarantines) all selected non-keeper members of <paramref name="group"/>.
    /// Validates size + mtime + identity before touching each file. Files that changed
    /// since the scan are skipped silently (not deleted).
    /// Returns total bytes freed/moved.
    /// </summary>
    Task<long> DeleteSelectedAsync(
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
