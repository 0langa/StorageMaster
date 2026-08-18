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
    Task<SmartCleanAnalysisResult> AnalyzeAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the paths in the supplied groups using the specified method.
    /// Returns exact success/failure details. Unsafe or stale paths fail closed.
    /// </summary>
    Task<SmartCleanResult> CleanAsync(
        IReadOnlyList<SmartCleanGroup> groups,
        DeletionMethod method,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public enum SmartCleanSource
{
    TemporaryFiles,
    BrowserCache,
    WindowsUpdateCache,
    WindowsErrorReporting,
    DeliveryOptimizationCache,
    ThumbnailCache,
    DirectXShaderCache,
}

/// <summary>A category of junk found by the Smart Cleaner.</summary>
public sealed record SmartCleanGroup(
    SmartCleanSource Source,
    string Category,
    string Description,
    string IconGlyph,
    long EstimatedBytes,
    IReadOnlyList<string> Paths,
    IReadOnlyDictionary<string, FileSnapshot> ExpectedFileSnapshots,
    bool IsSelected = true);

public sealed record SmartCleanFailure(string Path, string Error);

/// <summary>Read-only Smart Cleaner findings plus path-specific scan warnings.</summary>
public sealed record SmartCleanAnalysisResult(
    IReadOnlyList<SmartCleanGroup> Groups,
    IReadOnlyList<NoFollowFileEnumerationError> Warnings)
{
    public bool IsPartial => Warnings.Count > 0;
}

/// <summary>Aggregate result with per-path failures and audit-log warnings.</summary>
public sealed record SmartCleanResult(
    long BytesFreed,
    long BytesProcessed,
    int SuccessfulPathCount,
    IReadOnlyList<SmartCleanFailure> Failures,
    IReadOnlyList<string> AuditWarnings,
    bool WasCancelled = false,
    string? ErrorMessage = null)
{
    public bool AllDeletionsSucceeded =>
        !WasCancelled && ErrorMessage is null && Failures.Count == 0;

    public bool IsFullySuccessful =>
        AllDeletionsSucceeded && AuditWarnings.Count == 0;
}
