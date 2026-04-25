namespace StorageMaster.Core.Models;

public enum CleanupRisk { Safe, Low, Medium, High }

/// <summary>
/// A single actionable cleanup opportunity. Rules produce these; the UI presents them.
/// A suggestion is never acted upon without explicit user confirmation.
/// </summary>
public sealed record CleanupSuggestion
{
    public required Guid   Id            { get; init; }
    public required string RuleId        { get; init; }
    public required string Title         { get; init; }
    public required string Description   { get; init; }
    public required CleanupCategory Category { get; init; }
    public required CleanupRisk Risk     { get; init; }
    public required long   EstimatedBytes { get; init; }

    /// <summary>
    /// Paths that will be deleted or emptied. May be files or directories.
    /// The IFileDeleter implementation decides how to handle each.
    /// </summary>
    public required IReadOnlyList<string> TargetPaths { get; init; }

    /// <summary>When true, deletion of system-owned paths is involved — label clearly in UI.</summary>
    public bool IsSystemPath { get; init; }
}
