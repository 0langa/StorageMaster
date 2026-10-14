using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// The pieces of the "why was this not deleted" report that do not need a window:
/// which string explains a reason, what a group that failed as a whole leaves
/// behind, and which members a permanent retry of Recycle Bin refusals may touch.
/// Kept here rather than in the view model so they are tested.
/// </summary>
public static class DuplicateDeletionSkips
{
    /// <summary>Resource key explaining <paramref name="reason"/>; one exists per value in every UI language.</summary>
    public static string ResourceKey(DuplicateDeletionSkipReason reason) =>
        $"Safety_Duplicates_NotDeleted_{reason}";

    /// <summary>
    /// A group whose keeper could not be verified deletes nothing, so every member
    /// the user selected in it was left in place for that reason.
    /// </summary>
    public static IReadOnlyList<DuplicateDeletionSkip> ForUnprocessedGroup(
        IReadOnlyList<DuplicateGroupMember> members,
        string? detail) =>
        members
            .Where(static member => member.IsSelected && !member.IsKeeper && member.ExistsNow)
            .Select(member => new DuplicateDeletionSkip(
                member.Id,
                member.FullPath,
                DuplicateDeletionSkipReason.KeeperNotVerified,
                detail))
            .ToArray();

    /// <summary>
    /// The group's members with only the Recycle Bin refusals selected, for a
    /// permanent retry the user has confirmed. Everything else — the keeper, files
    /// already recycled, files skipped for any other reason — is deselected, so the
    /// retry cannot reach a file the user was not asked about. The retry still goes
    /// through every live check of a normal deletion.
    /// </summary>
    public static IReadOnlyList<DuplicateGroupMember> SelectOnlyRecycleBinRefusals(
        IReadOnlyList<DuplicateGroupMember> members,
        IEnumerable<DuplicateDeletionSkip> skipped)
    {
        var refused = skipped
            .Where(static skip => skip.Reason == DuplicateDeletionSkipReason.RecycleBinUnavailable)
            .Select(static skip => skip.MemberId)
            .ToHashSet();

        return members
            .Select(member => member with
            {
                IsSelected = !member.IsKeeper && member.IsSelected && refused.Contains(member.Id),
            })
            .ToArray();
    }
}
