using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// One pluggable cleanup rule. Rules analyze a completed (or in-progress) scan
/// session and emit zero or more suggestions. Rules MUST NOT delete anything.
/// </summary>
public interface ICleanupRule
{
    /// <summary>Stable identifier used for logging and deduplication.</summary>
    string RuleId { get; }

    string DisplayName { get; }

    CleanupCategory Category { get; }

    /// <summary>
    /// Produce suggestions for the given scan session.
    /// Implementations should be fast — defer expensive I/O to suggestion execution.
    /// </summary>
    IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        CancellationToken cancellationToken = default);
}
