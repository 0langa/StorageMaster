using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Duplicate deletion is intentionally unavailable through generic cleanup.
/// It requires live signature revalidation, keeper locking, recovery journaling,
/// and per-member outcomes supplied by <see cref="IDuplicateDeletionService"/>.
/// Users execute duplicate actions from dedicated Duplicates workflow.
/// </summary>
public sealed class DuplicateFilesCleanupRule(IDuplicateRepository duplicateRepository) : ICleanupRule
{
    public string RuleId => "duplicates.cleanup";
    public string DisplayName => Loc.Get("Rule_DuplicateFiles_Name");
    public CleanupCategory Category => CleanupCategory.DuplicateFiles;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = duplicateRepository;
        _ = sessionId;
        _ = settings;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}
