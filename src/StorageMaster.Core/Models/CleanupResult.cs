namespace StorageMaster.Core.Models;

public enum CleanupResultStatus { Success, PartialSuccess, Failed, Skipped }

/// <summary>Original-path → quarantine-path pair for a quarantined file.</summary>
public sealed record QuarantineMove(string OriginalPath, string QuarantinePath);

/// <summary>Outcome of executing a single CleanupSuggestion.</summary>
public sealed record CleanupResult
{
    public required Guid SuggestionId { get; init; }
    public required CleanupResultStatus Status { get; init; }
    public required long BytesFreed { get; init; }
    public required DateTime ExecutedUtc { get; init; }
    public required bool WasDryRun { get; init; }
    public IReadOnlyList<string> FailedPaths { get; init; } = [];
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Populated when the deletion method was Quarantine so the destination of
    /// every moved file is preserved in the audit trail for manual recovery.
    /// </summary>
    public IReadOnlyList<QuarantineMove> QuarantinedPaths { get; init; } = [];
}
