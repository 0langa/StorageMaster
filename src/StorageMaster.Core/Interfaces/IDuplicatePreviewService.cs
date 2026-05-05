using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicatePreviewService
{
    Task<DuplicatePreviewResult> BuildPreviewAsync(
        DuplicateMethod method,
        IReadOnlyList<DuplicateGroupMember> members,
        CancellationToken ct = default);
}
