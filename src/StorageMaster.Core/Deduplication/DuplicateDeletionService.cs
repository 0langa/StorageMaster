using System.Text.Json;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

public sealed class DuplicateDeletionService(
    IFileDeleter deleter,
    ICleanupLogRepository cleanupLogRepository,
    IDuplicateRepository duplicateRepository) : IDuplicateDeletionService
{
    public async Task<long> DeleteSelectedAsync(
        DuplicateGroup group,
        IReadOnlyList<DuplicateGroupMember> members,
        DeletionMethod method,
        CancellationToken ct = default)
    {
        var selected = members.Where(static member => member.IsSelected && !member.IsKeeper && member.ExistsNow).ToList();
        if (selected.Count == 0)
            return 0;

        long freed = 0;
        var deletedMemberIds = new List<long>(selected.Count);

        foreach (var member in selected)
        {
            var info = new FileInfo(member.FullPath);
            if (!info.Exists || info.Length != member.SizeBytes || info.LastWriteTimeUtc != member.ModifiedUtc.ToUniversalTime())
                continue;

            var outcome = await deleter.DeleteAsync(new DeletionRequest(member.FullPath, method, DryRun: false), ct);
            if (!outcome.Success)
                continue;

            freed += outcome.BytesFreed;
            deletedMemberIds.Add(member.Id);

            var suggestion = new CleanupSuggestion
            {
                Id = Guid.NewGuid(),
                RuleId = $"duplicates.{group.Method}".ToLowerInvariant(),
                Title = $"Duplicate file: {member.FileName}",
                Description = $"Duplicate file selected from duplicate group {group.Id}.",
                Category = CleanupCategory.DuplicateFiles,
                Risk = CleanupRisk.Low,
                EstimatedBytes = member.SizeBytes,
                TargetPaths = [member.FullPath],
                IsSystemPath = false,
                AuditDataJson = JsonSerializer.Serialize(new
                {
                    DuplicateGroupId = group.Id,
                    group.RunId,
                    DuplicateMethod = group.Method.ToString(),
                    group.Confidence,
                    KeeperFileEntryId = members.FirstOrDefault(static candidate => candidate.IsKeeper)?.FileEntryId,
                    DeletedFileEntryId = member.FileEntryId,
                    member.FullPath,
                    DeletionMethod = method.ToString(),
                }),
            };

            var result = new CleanupResult
            {
                SuggestionId = suggestion.Id,
                Status = CleanupResultStatus.Success,
                BytesFreed = outcome.BytesFreed,
                ExecutedUtc = DateTime.UtcNow,
                WasDryRun = false,
            };

            await cleanupLogRepository.LogResultAsync(result, suggestion, ct);
        }

        if (deletedMemberIds.Count > 0)
            await duplicateRepository.MarkMembersDeletedAsync(deletedMemberIds, ct);

        return freed;
    }
}
