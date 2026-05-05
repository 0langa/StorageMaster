using System.Text.Json;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

public sealed class DuplicateFilesCleanupRule(IDuplicateRepository duplicateRepository) : ICleanupRule
{
    public string RuleId => "duplicates.cleanup";
    public string DisplayName => "Duplicate Files";
    public CleanupCategory Category => CleanupCategory.DuplicateFiles;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var latestRun = (await duplicateRepository.GetRunsForSessionAsync(sessionId, cancellationToken))
            .FirstOrDefault(static run => run.Status == DuplicateRunStatus.Completed);
        if (latestRun is null)
            yield break;

        var groups = await duplicateRepository.GetGroupsForRunAsync(latestRun.Id, cancellationToken);
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var members = await duplicateRepository.GetMembersForGroupAsync(group.Id, cancellationToken);
            var selected = members.Where(static member => member.IsSelected && member.ExistsNow && !member.IsKeeper).ToList();
            if (selected.Count == 0)
                continue;

            var keeper = members.FirstOrDefault(static member => member.IsKeeper);
            yield return new CleanupSuggestion
            {
                Id = Guid.NewGuid(),
                RuleId = RuleId,
                Title = $"Duplicate group ({selected.Count} removable file(s))",
                Description = keeper is null
                    ? $"Duplicate files detected by {group.Method}."
                    : $"Keep {keeper.FileName}; remove selected duplicate copies found by {group.Method}.",
                Category = CleanupCategory.DuplicateFiles,
                Risk = CleanupRisk.Low,
                EstimatedBytes = selected.Sum(static member => member.SizeBytes),
                TargetPaths = selected.Select(static member => member.FullPath).ToArray(),
                IsSystemPath = false,
                AuditDataJson = JsonSerializer.Serialize(new
                {
                    DuplicateRunId = latestRun.Id,
                    DuplicateGroupId = group.Id,
                    group.Method,
                    group.Confidence,
                    KeeperPath = keeper?.FullPath,
                }),
            };
        }
    }
}
