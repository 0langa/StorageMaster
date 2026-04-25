using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>Orchestrates all registered ICleanupRule instances.</summary>
public interface ICleanupEngine
{
    /// <summary>Runs all rules against the session and aggregates suggestions.</summary>
    IAsyncEnumerable<CleanupSuggestion> GetSuggestionsAsync(
        long              sessionId,
        AppSettings       settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the selected suggestions.
    /// Requires explicit user confirmation — this method MUST NOT be called
    /// without a preceding confirmation dialog in the UI layer.
    /// </summary>
    Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
        IReadOnlyList<CleanupSuggestion> suggestions,
        bool              dryRun,
        CancellationToken cancellationToken = default);
}
