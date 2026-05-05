using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateDeletionService
{
    Task<long> DeleteSelectedAsync(
        DuplicateGroup group,
        IReadOnlyList<DuplicateGroupMember> members,
        DeletionMethod method,
        CancellationToken ct = default);
}
