using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup;

/// <summary>Central safety policy for cleanup suggestions selected before user review.</summary>
public static class CleanupSuggestionSelectionPolicy
{
    /// <summary>
    /// Only recoverable safe/low-risk suggestions may start selected. Medium/high-risk
    /// suggestions always require a deliberate per-item choice in the review UI.
    /// </summary>
    public static bool ShouldSelectByDefault(CleanupSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return suggestion.SupportsRecycleBin &&
            suggestion.Risk is CleanupRisk.Safe or CleanupRisk.Low;
    }
}
