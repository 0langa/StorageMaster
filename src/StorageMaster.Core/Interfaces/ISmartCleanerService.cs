using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// All-in-one quick scan + cleanup that operates directly on well-known junk
/// locations without requiring a prior full disk scan.
/// </summary>
public interface ISmartCleanerService
{
    /// <summary>
    /// Scans well-known junk sources and returns grouped findings.
    /// This is read-only — nothing is deleted until <see cref="CleanAsync"/> is called.
    /// </summary>
    Task<IReadOnlyList<SmartCleanGroup>> AnalyzeAsync(
        IProgress<string>? progress = null,
        CancellationToken  cancellationToken = default);

    /// <summary>
    /// Deletes the paths in the supplied groups using the specified method.
    /// Returns bytes actually freed (best-effort).
    /// </summary>
    Task<long> CleanAsync(
        IReadOnlyList<SmartCleanGroup> groups,
        DeletionMethod                 method,
        IProgress<string>?             progress          = null,
        CancellationToken              cancellationToken = default);
}

/// <summary>A category of junk found by the Smart Cleaner.</summary>
public sealed record SmartCleanGroup(
    string                  Category,
    string                  Description,
    string                  IconGlyph,
    long                    EstimatedBytes,
    IReadOnlyList<string>   Paths,
    bool                    IsSelected = true);
