using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateRepository
{
    Task<DuplicateRun> CreateRunAsync(DuplicateScanOptions options, CancellationToken ct = default);
    Task CompleteRunAsync(
        long runId,
        DuplicateRunStatus status,
        long candidateCount,
        long groupCount,
        long exactBytes,
        long reclaimableBytes,
        long errorCount,
        string? errorMessage = null,
        CancellationToken ct = default);
    Task SaveResultsAsync(
        long runId,
        IReadOnlyList<DuplicateSignature> signatures,
        IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyList<DuplicateGroupMember> members,
        IReadOnlyList<DuplicateError> errors,
        CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateRun>> GetRunsForSessionAsync(long sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateGroup>> GetGroupsForRunAsync(long runId, CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateGroupMember>> GetMembersForGroupAsync(long groupId, CancellationToken ct = default);
    Task MarkMembersDeletedAsync(IReadOnlyList<long> memberIds, CancellationToken ct = default);
}
