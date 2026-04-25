namespace StorageMaster.Core.Interfaces;

public enum DeletionMethod { RecycleBin, Permanent }

public sealed record DeletionRequest(
    string         Path,
    DeletionMethod Method,
    bool           DryRun);

public sealed record DeletionOutcome(
    string  Path,
    bool    Success,
    long    BytesFreed,
    string? Error = null);

/// <summary>
/// Platform abstraction for file/folder deletion.
/// All deletion paths flow through this interface so they can be logged,
/// tested, and swapped (e.g., for a mock in tests).
/// </summary>
public interface IFileDeleter
{
    /// <summary>
    /// Deletes or recycles a single path. Never throws for per-file errors —
    /// returns a failed DeletionOutcome instead.
    /// </summary>
    Task<DeletionOutcome> DeleteAsync(DeletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Batch overload — processes in parallel with bounded concurrency.</summary>
    IAsyncEnumerable<DeletionOutcome> DeleteManyAsync(
        IReadOnlyList<DeletionRequest> requests,
        CancellationToken              cancellationToken = default);
}
