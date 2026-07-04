using System.Text.Json;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Deletes or quarantines selected duplicate group members with safety guards:
///
///   1. Snapshot validation: size + mtime must match what was recorded at scan time.
///   2. Keeper safety: refuses deletion when no keeper exists or keeper is missing.
///   3. Changed-file skipping: files that moved or changed since scan are skipped.
///   4. Audit logging: every deletion/quarantine emitted to <see cref="ICleanupLogRepository"/>.
///   5. Quarantine restore: moves file back to original (or alternate) path.
/// </summary>
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
        // ── Safety: refuse when keeper is absent ──────────────────────────
        var keeper = members.FirstOrDefault(static m => m.IsKeeper);
        if (keeper is null || !File.Exists(keeper.FullPath))
            throw new InvalidOperationException(
                $"Group {group.Id}: keeper file is missing or not designated. " +
                "Cannot delete duplicates without a confirmed keeper.");

        var selected = members
            .Where(static m => m.IsSelected && !m.IsKeeper && m.ExistsNow)
            .ToList();
        if (selected.Count == 0)
            return 0L;

        var validated = new List<(DuplicateGroupMember Member, DeletionRequest Request, DuplicateOperationJournalEntry Journal)>(selected.Count);
        foreach (var member in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsSafeToDelete(member))
                continue;

            var request = new DeletionRequest(
                member.FullPath,
                method,
                DryRun: false,
                QuarantineRunId: group.RunId);
            var journal = await duplicateRepository.RecordDuplicateOperationIntentAsync(
                CreateDeleteJournal(group, member, method),
                ct);

            validated.Add((member, request, journal));
        }

        if (validated.Count == 0)
            return 0L;

        long freed = 0L;
        var deletedMemberIds = new List<long>(validated.Count);
        var membersByPath = validated.ToDictionary(
            static item => item.Member.FullPath,
            static item => (item.Member, item.Journal),
            StringComparer.OrdinalIgnoreCase);

        await foreach (var outcome in deleter.DeleteManyAsync(validated.Select(static item => item.Request).ToList(), ct))
        {
            if (!membersByPath.TryGetValue(outcome.Path, out var tracked))
                continue;

            var (member, journal) = tracked;
            if (!outcome.Success)
            {
                await duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                    journal.Id,
                    DuplicateOperationStatus.Failed,
                    outcome.QuarantinePath,
                    outcome.BytesFreed,
                    outcome.Error ?? "Deletion failed.",
                    ct);
                continue;
            }

            freed += outcome.BytesFreed;
            deletedMemberIds.Add(member.Id);

            if (method == DeletionMethod.Quarantine && outcome.QuarantinePath is not null)
            {
                await duplicateRepository.RecordQuarantineAsync(
                    member.Id, group.RunId,
                    member.FullPath, outcome.QuarantinePath, ct);
            }

            await duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                journal.Id,
                method == DeletionMethod.Quarantine ? DuplicateOperationStatus.Quarantined : DuplicateOperationStatus.Completed,
                outcome.QuarantinePath,
                outcome.BytesFreed,
                outcome.Error,
                ct);
            await LogAuditAsync(group, member, keeper, method, outcome, ct);
        }

        if (deletedMemberIds.Count > 0)
            await duplicateRepository.MarkMembersDeletedAsync(deletedMemberIds, ct);

        return freed;
    }

    public async Task RestoreFromQuarantineAsync(
        long quarantineId,
        string? targetPath = null,
        CancellationToken ct = default)
    {
        var record = await duplicateRepository.GetQuarantinedFileAsync(quarantineId, ct)
            ?? throw new InvalidOperationException($"Quarantine record {quarantineId} was not found.");

        if (record.RestoredUtc is not null)
            throw new InvalidOperationException($"Quarantine record {quarantineId} was already restored.");

        if (!File.Exists(record.QuarantinePath))
            throw new FileNotFoundException("Quarantined file is missing.", record.QuarantinePath);

        var restorePath = string.IsNullOrWhiteSpace(targetPath) ? record.OriginalPath : targetPath;
        var restoreDirectory = Path.GetDirectoryName(restorePath);
        if (!string.IsNullOrWhiteSpace(restoreDirectory))
            Directory.CreateDirectory(restoreDirectory);

        if (File.Exists(restorePath))
            throw new IOException($"Restore target already exists: {restorePath}");

        var journal = await duplicateRepository.RecordDuplicateOperationIntentAsync(
            CreateRestoreJournal(record, restorePath),
            ct);

        try
        {
            File.Move(record.QuarantinePath, restorePath);
            await duplicateRepository.MarkRestoredAsync(quarantineId, restorePath, ct);
            await duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                journal.Id,
                DuplicateOperationStatus.Restored,
                restorePath,
                0,
                null,
                ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                journal.Id,
                DuplicateOperationStatus.Failed,
                restorePath,
                null,
                ex.Message,
                ct);
            throw;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DuplicateOperationJournalEntry CreateDeleteJournal(
        DuplicateGroup group,
        DuplicateGroupMember member,
        DeletionMethod method) => new()
        {
            OperationId = Guid.NewGuid(),
            Kind = DuplicateOperationKind.Delete,
            Status = DuplicateOperationStatus.Planned,
            RunId = group.RunId,
            GroupId = group.Id,
            MemberId = member.Id,
            Method = method,
            SourcePath = member.FullPath,
            SourceSizeBytes = member.SizeBytes,
            SourceModifiedUtc = member.ModifiedUtc.ToUniversalTime(),
            PlannedUtc = DateTime.UtcNow,
            MetadataJson = JsonSerializer.Serialize(new
            {
                group.Method,
                group.Algorithm,
                group.Confidence,
                member.FileEntryId,
                member.RecommendationReason,
            }),
        };

    private static DuplicateOperationJournalEntry CreateRestoreJournal(
        QuarantinedFile record,
        string restorePath) => new()
        {
            OperationId = Guid.NewGuid(),
            Kind = DuplicateOperationKind.Restore,
            Status = DuplicateOperationStatus.Planned,
            RunId = record.RunId,
            MemberId = record.MemberId,
            QuarantineId = record.Id,
            Method = DeletionMethod.Quarantine,
            SourcePath = record.QuarantinePath,
            DestinationPath = restorePath,
            SourceSizeBytes = File.Exists(record.QuarantinePath) ? new FileInfo(record.QuarantinePath).Length : 0,
            SourceModifiedUtc = File.Exists(record.QuarantinePath) ? File.GetLastWriteTimeUtc(record.QuarantinePath) : null,
            PlannedUtc = DateTime.UtcNow,
            MetadataJson = JsonSerializer.Serialize(new
            {
                record.OriginalPath,
                RestorePath = restorePath,
            }),
        };

    private static bool IsSafeToDelete(DuplicateGroupMember member)
    {
        var info = new FileInfo(member.FullPath);
        if (!info.Exists)
            return false;

        return info.Length == member.SizeBytes &&
               info.LastWriteTimeUtc.TruncateToSeconds() == member.ModifiedUtc.ToUniversalTime().TruncateToSeconds();
    }

    private async Task LogAuditAsync(
        DuplicateGroup group,
        DuplicateGroupMember member,
        DuplicateGroupMember keeper,
        DeletionMethod method,
        DeletionOutcome outcome,
        CancellationToken ct)
    {
        var suggestion = new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = $"duplicates.{group.Method}".ToLowerInvariant(),
            Title = $"Duplicate file: {member.FileName}",
            Description = $"Duplicate file selected from group {group.Id} via {method}.",
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
                KeeperFileEntryId = keeper.FileEntryId,
                KeeperPath = keeper.FullPath,
                DeletedFileEntryId = member.FileEntryId,
                member.FullPath,
                member.SizeBytes,
                member.ModifiedUtc,
                DeletionMethod = method.ToString(),
                outcome.Success,
                outcome.BytesFreed,
                outcome.Error,
                QuarantinePath = outcome.QuarantinePath,
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
}

file static class DateTimeExtensions
{
    /// <summary>Truncates to 1-second precision for mtime comparison (FAT / NTFS granularity).</summary>
    internal static DateTime TruncateToSeconds(this DateTime dt) =>
        new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, dt.Kind);
}
