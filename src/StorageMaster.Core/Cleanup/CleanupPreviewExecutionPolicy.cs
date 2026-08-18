using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup;

/// <summary>
/// Determines whether a dry-run proved every selected suggestion safe to offer
/// as an immediate real-deletion follow-up.
/// </summary>
public static class CleanupPreviewExecutionPolicy
{
    public static bool CanExecuteAfterPreview(
        IReadOnlyList<CleanupResult> results,
        IReadOnlyCollection<Guid> expectedSuggestionIds)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(expectedSuggestionIds);

        if (expectedSuggestionIds.Count == 0 || results.Count != expectedSuggestionIds.Count)
            return false;

        var expectedIds = expectedSuggestionIds.ToHashSet();
        if (expectedIds.Count != expectedSuggestionIds.Count)
            return false;

        var actualIds = new HashSet<Guid>();
        foreach (var result in results)
        {
            if (!actualIds.Add(result.SuggestionId) ||
                !expectedIds.Contains(result.SuggestionId) ||
                !result.WasDryRun ||
                result.Status != CleanupResultStatus.Success ||
                result.FailedPaths.Count != 0 ||
                !string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                return false;
            }
        }

        return actualIds.SetEquals(expectedIds);
    }
}
