using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Narrow write-side surface for recording quarantine moves. Implemented by
/// the duplicate repository; consumed by the generic cleanup engine so it can
/// create restore records without depending on the full duplicate repository.
/// </summary>
public interface IQuarantineRecorder
{
    /// <summary>
    /// Records a quarantine move so the file can be restored later.
    /// <paramref name="memberId"/> is null for generic-cleanup quarantines
    /// (no duplicate group member); those use <see cref="GenericCleanupRunId"/>.
    /// </summary>
    Task<QuarantinedFile> RecordQuarantineAsync(
        long? memberId,
        long runId,
        string originalPath,
        string quarantinePath,
        CancellationToken ct = default);

    /// <summary>
    /// RunId used for quarantines that do not belong to a duplicate run —
    /// matches the Quarantine\0\… folder the file deleter uses when no
    /// QuarantineRunId is supplied.
    /// </summary>
    public const long GenericCleanupRunId = 0;
}
