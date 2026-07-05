using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateRepository : IQuarantineRecorder
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
    // RecordQuarantineAsync is inherited from IQuarantineRecorder.

    Task<IReadOnlyList<QuarantinedFile>> GetQuarantinedFilesAsync(
        long runId,
        CancellationToken ct = default);

    /// <summary>
    /// All quarantined files not yet restored, across duplicate runs and
    /// generic cleanup, newest first — backs the "All quarantined files" view.
    /// </summary>
    Task<IReadOnlyList<QuarantinedFile>> GetUnrestoredQuarantinedFilesAsync(
        CancellationToken ct = default);

    Task<QuarantinedFile?> GetQuarantinedFileAsync(
        long quarantineId,
        CancellationToken ct = default);

    Task MarkRestoredAsync(
        long quarantineId,
        string restoredPath,
        CancellationToken ct = default);

    // ── Recovery journal ───────────────────────────────────────────────────

    Task<DuplicateOperationJournalEntry> RecordDuplicateOperationIntentAsync(
        DuplicateOperationJournalEntry entry,
        CancellationToken ct = default);

    Task UpdateDuplicateOperationOutcomeAsync(
        long journalId,
        DuplicateOperationStatus status,
        string? destinationPath,
        long? bytesFreed,
        string? errorMessage,
        CancellationToken ct = default);

    Task<IReadOnlyList<DuplicateOperationJournalEntry>> GetDuplicateOperationJournalAsync(
        long runId,
        CancellationToken ct = default);
}
