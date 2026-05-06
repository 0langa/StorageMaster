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

    /// <summary>
    /// Upserts signatures and inserts groups/members/errors for a completed run.
    /// Signature upsert is idempotent: ON CONFLICT(FileEntryId,Method,Algorithm) DO UPDATE.
    /// </summary>
    Task SaveResultsAsync(
        long runId,
        IReadOnlyList<DuplicateSignature> signatures,
        IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyList<DuplicateGroupMember> members,
        IReadOnlyList<DuplicateError> errors,
        CancellationToken ct = default);

    Task<IReadOnlyList<DuplicateRun>> GetRunsForSessionAsync(long sessionId, CancellationToken ct = default);
    Task<DuplicateRunSummary> GetDuplicateRunSummaryAsync(long runId, CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsPageAsync(
        long runId,
        int page,
        int pageSize,
        DuplicateGroupQueryFilter? filters,
        DuplicateGroupSortBy sortBy,
        CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateError>> GetDuplicateErrorsPageAsync(
        long runId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    Task<IReadOnlyList<DuplicateGroup>> GetGroupsForRunAsync(long runId, CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateGroupMember>> GetDuplicateGroupMembersAsync(long groupId, CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateGroupMember>> GetMembersForGroupAsync(long groupId, CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateError>> GetErrorsForRunAsync(long runId, CancellationToken ct = default);
    Task MarkMembersDeletedAsync(IReadOnlyList<long> memberIds, CancellationToken ct = default);

    // ── Signature cache ──────────────────────────────────────────────────────

    /// <summary>
    /// Load existing signatures for a session+method+algorithm combination so
    /// the pipeline can skip files whose cached signatures are still valid.
    /// </summary>
    Task<IReadOnlyList<DuplicateSignature>> GetCachedSignaturesAsync(
        long sessionId,
        DuplicateMethod method,
        string algorithm,
        int algorithmVersion,
        CancellationToken ct = default);

    // ── Quarantine ───────────────────────────────────────────────────────────

    Task<QuarantinedFile> RecordQuarantineAsync(
        long memberId,
        long runId,
        string originalPath,
        string quarantinePath,
        CancellationToken ct = default);

    Task<IReadOnlyList<QuarantinedFile>> GetQuarantinedFilesAsync(
        long runId,
        CancellationToken ct = default);

    Task<QuarantinedFile?> GetQuarantinedFileAsync(
        long quarantineId,
        CancellationToken ct = default);

    Task MarkRestoredAsync(
        long quarantineId,
        string restoredPath,
        CancellationToken ct = default);
}
