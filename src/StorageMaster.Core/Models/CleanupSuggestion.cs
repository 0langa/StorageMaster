namespace StorageMaster.Core.Models;

public enum CleanupRisk { Safe, Low, Medium, High }

/// <summary>
/// A single actionable cleanup opportunity. Rules produce these; the UI presents them.
/// A suggestion is never acted upon without explicit user confirmation.
/// </summary>
public sealed record CleanupSuggestion
{
    public required Guid Id { get; init; }
    public required string RuleId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required CleanupCategory Category { get; init; }
    public required CleanupRisk Risk { get; init; }
    public required long EstimatedBytes { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool SupportsPermanentDelete { get; init; } = true;
    public bool SupportsRecycleBin { get; init; } = true;
    public bool SupportsQuarantine { get; init; } = true;
    public bool NeedsServiceStop { get; init; }
    public double Confidence { get; init; } = 1.0;
    public string SafetyNotes { get; init; } = "Review the listed paths before execution.";

    /// <summary>
    /// Paths that will be deleted or emptied. May be files or directories.
    /// The IFileDeleter implementation decides how to handle each.
    /// </summary>
    public required IReadOnlyList<string> TargetPaths { get; init; }

    /// <summary>
    /// Optional scan-time state keyed by target path. Deletion fails closed when
    /// current size, timestamp, attributes, or available identity no longer match.
    /// </summary>
    public IReadOnlyDictionary<string, FileSnapshot> ExpectedFileSnapshots { get; init; } =
        new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, deletion of system-owned paths is involved — label clearly in UI.</summary>
    public bool IsSystemPath { get; init; }

    /// <summary>Optional JSON metadata written to the cleanup audit log.</summary>
    public string? AuditDataJson { get; init; }
}
