using System.Globalization;
using System.Text.Json;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Deletes or quarantines selected duplicate group members with safety guards:
///
///   1. Live signature validation: keeper and selected file must still match.
///   2. Keeper safety: keeper is revalidated and locked against writes/replacement.
///   3. Changed-file skipping: file snapshots are checked again at deletion boundary.
///   4. Audit logging: every deletion/quarantine emitted to <see cref="ICleanupLogRepository"/>.
///   5. Quarantine restore: moves file back to original (or alternate) path.
/// </summary>
public sealed class DuplicateDeletionService : IDuplicateDeletionService
{
    private readonly IFileDeleter _deleter;
    private readonly ICleanupLogRepository _cleanupLogRepository;
    private readonly IDuplicateRepository _duplicateRepository;
    private readonly IFileSnapshotProvider _snapshotProvider;
    private readonly IReadOnlyDictionary<DuplicateMethod, IDuplicateDetectionStrategy> _strategies;

    public DuplicateDeletionService(
        IFileDeleter deleter,
        ICleanupLogRepository cleanupLogRepository,
        IDuplicateRepository duplicateRepository,
        IFileSnapshotProvider snapshotProvider,
        IEnumerable<IDuplicateDetectionStrategy> strategies)
    {
        _deleter = deleter;
        _cleanupLogRepository = cleanupLogRepository;
        _duplicateRepository = duplicateRepository;
        _snapshotProvider = snapshotProvider;
        _strategies = strategies.ToDictionary(static strategy => strategy.Method);
    }

    public async Task<long> DeleteSelectedAsync(
        DuplicateGroup group,
        IReadOnlyList<DuplicateGroupMember> members,
        DeletionMethod method,
        CancellationToken ct = default) =>
        (await DeleteSelectedWithResultAsync(group, members, method, ct).ConfigureAwait(false)).ProcessedBytes;

    public async Task<DuplicateDeletionResult> DeleteSelectedWithResultAsync(
        DuplicateGroup group,
        IReadOnlyList<DuplicateGroupMember> members,
        DeletionMethod method,
        CancellationToken ct = default)
    {
        var keeper = members.FirstOrDefault(static m => m.IsKeeper);
        if (keeper is null || !File.Exists(keeper.FullPath))
            throw new InvalidOperationException(
                $"Group {group.Id}: keeper file is missing or not designated. " +
                "Cannot delete duplicates without a confirmed keeper.");

        var selected = members
            .Where(static m => m.IsSelected && !m.IsKeeper && m.ExistsNow)
            .ToList();
        if (selected.Count == 0)
            return new DuplicateDeletionResult(0, 0, []);

        if (!_strategies.TryGetValue(group.Method, out var strategy) || !strategy.IsAvailable)
        {
            throw new InvalidOperationException(
                $"Group {group.Id}: {group.Method} cannot be revalidated in the current environment.");
        }

        if (!string.Equals(group.Algorithm, strategy.Algorithm, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Group {group.Id}: stored algorithm '{group.Algorithm}' does not match " +
                $"available verifier '{strategy.Algorithm}'. Run duplicate analysis again.");
        }

        if (keeper.Identity is null)
        {
            throw new InvalidOperationException(
                $"Group {group.Id}: keeper has no stable scan-time file identity. " +
                "Run duplicate analysis again before deleting files.");
        }

        await using var keeperLock = OpenKeeperLock(keeper);
        var keeperSnapshot = await _snapshotProvider.TakeSnapshotAsync(keeper.FullPath, ct);
        if (keeperSnapshot is null || !MatchesRecordedState(keeper, keeperSnapshot))
        {
            throw new InvalidOperationException(
                $"Group {group.Id}: keeper changed since duplicate analysis. Run analysis again before deleting files.");
        }

        var keeperCandidate = CreateLiveCandidate(keeper, keeperSnapshot);
        var keeperSignature = await strategy.ComputeSignatureAsync(keeperCandidate, ct);
        if (!IsReady(keeperSignature))
        {
            throw new InvalidOperationException(
                $"Group {group.Id}: keeper could not be revalidated ({keeperSignature.ErrorMessage ?? "signature unavailable"}).");
        }

        long processedBytes = 0L;
        var deletedMembers = new List<DuplicateGroupMember>(selected.Count);
        var warnings = new List<DuplicateDeletionWarning>();
        foreach (var member in selected)
        {
            ct.ThrowIfCancellationRequested();

            if (member.Identity is null)
            {
                warnings.Add(new DuplicateDeletionWarning(
                    member.FullPath,
                    "Skipped because no stable scan-time file identity is available. Run duplicate analysis again."));
                continue;
            }

            var memberSnapshot = await _snapshotProvider.TakeSnapshotAsync(member.FullPath, ct);
            if (memberSnapshot is null || !MatchesRecordedState(member, memberSnapshot))
                continue;

            var memberCandidate = CreateLiveCandidate(member, memberSnapshot);
            var memberSignature = await strategy.ComputeSignatureAsync(memberCandidate, ct);
            if (!IsReady(memberSignature) ||
                !AreLiveDuplicates(strategy, keeperCandidate, keeperSignature, memberCandidate, memberSignature))
            {
                continue;
            }

            var request = new DeletionRequest(
                member.FullPath,
                method,
                DryRun: false,
                QuarantineRunId: group.RunId,
                ExpectedSnapshot: memberSnapshot);
            var journal = await _duplicateRepository.RecordDuplicateOperationIntentAsync(
                CreateDeleteJournal(group, member, method),
                ct);

            var outcome = await _deleter.DeleteAsync(request, ct);
            if (!outcome.Success)
            {
                await _duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                    journal.Id,
                    DuplicateOperationStatus.Failed,
                    outcome.QuarantinePath,
                    outcome.BytesFreed,
                    outcome.Error ?? "Deletion failed.",
                    CancellationToken.None);
                continue;
            }

            if (method == DeletionMethod.Quarantine)
            {
                if (string.IsNullOrWhiteSpace(outcome.QuarantinePath))
                {
                    throw new InvalidOperationException(
                        $"Quarantine move for '{member.FullPath}' succeeded without returning its destination path.");
                }

                try
                {
                    await _duplicateRepository.CompleteQuarantineMoveAsync(
                        journal.Id,
                        member.Id,
                        group.RunId,
                        member.FullPath,
                        outcome.QuarantinePath,
                        outcome.BytesFreed,
                        CancellationToken.None);
                }
                catch (Exception persistenceError)
                {
                    // The file has already moved. Preserve its exact source/destination in
                    // the terminal journal before any later bookkeeping is attempted.
                    try
                    {
                        await _duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                            journal.Id,
                            DuplicateOperationStatus.Quarantined,
                            outcome.QuarantinePath,
                            outcome.BytesFreed,
                            persistenceError.Message,
                            CancellationToken.None);
                    }
                    catch (Exception journalError)
                    {
                        throw new InvalidOperationException(
                            $"File moved to quarantine but recovery bookkeeping failed. " +
                            $"Original: '{member.FullPath}'. Destination: '{outcome.QuarantinePath}'.",
                            new AggregateException(persistenceError, journalError));
                    }

                    warnings.Add(new DuplicateDeletionWarning(
                        member.FullPath,
                        $"Quarantine catalog update failed after the move; the terminal recovery journal " +
                        $"retains destination '{outcome.QuarantinePath}'. {persistenceError.Message}"));
                }
            }
            else
            {
                try
                {
                    await _duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                        journal.Id,
                        DuplicateOperationStatus.Completed,
                        outcome.QuarantinePath,
                        outcome.BytesFreed,
                        outcome.Error,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // The filesystem mutation already succeeded. Report degraded
                    // bookkeeping, but never misreport the file as untouched or retry it.
                    warnings.Add(new DuplicateDeletionWarning(
                        member.FullPath,
                        $"Filesystem deletion succeeded, but its terminal recovery journal " +
                        $"could not be updated: {ex.Message}"));
                }
            }

            // This is the validated amount operated on, not claimed physical space
            // reclamation. Recycle Bin and quarantine moves normally free zero bytes.
            processedBytes += memberSnapshot.SizeBytes;
            deletedMembers.Add(member);

            try
            {
                await LogAuditAsync(group, member, keeper, method, outcome, CancellationToken.None);
            }
            catch (Exception ex)
            {
                warnings.Add(new DuplicateDeletionWarning(
                    member.FullPath,
                    $"Deletion succeeded, but its cleanup audit could not be written: {ex.Message}"));
            }
        }

        if (deletedMembers.Count > 0)
        {
            try
            {
                await _duplicateRepository.MarkMembersDeletedAsync(
                    deletedMembers.Select(static member => member.Id).ToArray(),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                foreach (var member in deletedMembers)
                {
                    warnings.Add(new DuplicateDeletionWarning(
                        member.FullPath,
                        $"File deletion completed and its recovery journal is terminal, but duplicate-member state " +
                        $"could not be updated: {ex.Message}"));
                }
            }
        }

        return new DuplicateDeletionResult(processedBytes, deletedMembers.Count, warnings.ToArray());
    }

    public async Task RestoreFromQuarantineAsync(
        long quarantineId,
        string? targetPath = null,
        CancellationToken ct = default)
    {
        var record = await _duplicateRepository.GetQuarantinedFileAsync(quarantineId, ct)
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

        var journal = await _duplicateRepository.RecordDuplicateOperationIntentAsync(
            CreateRestoreJournal(record, restorePath),
            ct);

        try
        {
            File.Move(record.QuarantinePath, restorePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                journal.Id,
                DuplicateOperationStatus.Failed,
                restorePath,
                null,
                ex.Message,
                CancellationToken.None);
            throw;
        }

        try
        {
            // The exact source/destination pair becomes terminal before the mutable
            // quarantine catalog row is touched. A later catalog failure is recoverable.
            await _duplicateRepository.UpdateDuplicateOperationOutcomeAsync(
                journal.Id,
                DuplicateOperationStatus.Restored,
                restorePath,
                0,
                null,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"File was restored, but its recovery journal could not be terminalized. " +
                $"Quarantine source: '{record.QuarantinePath}'. Restored destination: '{restorePath}'.",
                ex);
        }

        try
        {
            await _duplicateRepository.MarkRestoredAsync(quarantineId, restorePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"File was restored to '{restorePath}' and the terminal recovery journal was saved, " +
                $"but the quarantine catalog could not be updated: {ex.Message}",
                ex);
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
            SourceIdentity = SerializeIdentity(member.Identity),
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

    private static FileStream OpenKeeperLock(DuplicateGroupMember keeper)
    {
        try
        {
            return new FileStream(
                keeper.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Keeper cannot be locked against changes: {keeper.FullPath}",
                ex);
        }
    }

    private static bool MatchesRecordedState(
        DuplicateGroupMember member,
        FileSnapshot snapshot) =>
        member.Identity is not null &&
        snapshot.Identity == member.Identity &&
        snapshot.SizeBytes == member.SizeBytes &&
        snapshot.LastWriteUtc == member.ModifiedUtc.ToUniversalTime() &&
        snapshot.Attributes == member.Attributes;

    private static string? SerializeIdentity(FileIdentity? identity) =>
        identity is null
            ? null
            : string.Concat(
                identity.VolumeSerial,
                ":",
                identity.FileIndex.ToString(CultureInfo.InvariantCulture));

    private static DuplicateCandidate CreateLiveCandidate(
        DuplicateGroupMember member,
        FileSnapshot snapshot)
    {
        var extension = Path.GetExtension(member.FullPath);
        return new DuplicateCandidate(
            new FileEntry
            {
                Id = member.FileEntryId,
                SessionId = 0,
                FullPath = member.FullPath,
                FileName = member.FileName,
                Extension = extension,
                SizeBytes = snapshot.SizeBytes,
                CreatedUtc = snapshot.LastWriteUtc,
                ModifiedUtc = snapshot.LastWriteUtc,
                AccessedUtc = snapshot.LastWriteUtc,
                Attributes = snapshot.Attributes,
                Category = FileTypeCategorizor.Categorize(extension),
                Identity = snapshot.Identity,
                IsReparsePoint = snapshot.Attributes.HasFlag(FileAttributes.ReparsePoint),
            },
            snapshot.Identity);
    }

    private static bool IsReady(DuplicateSignature signature) =>
        string.Equals(signature.Status, "Ready", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(signature.SignatureText);

    private static bool AreLiveDuplicates(
        IDuplicateDetectionStrategy strategy,
        DuplicateCandidate keeperCandidate,
        DuplicateSignature keeperSignature,
        DuplicateCandidate memberCandidate,
        DuplicateSignature memberSignature)
    {
        var pairs = new[]
        {
            (Signature: keeperSignature.SignatureText!, Candidate: keeperCandidate),
            (Signature: memberSignature.SignatureText!, Candidate: memberCandidate),
        };
        var groups = pairs
            .GroupBy(static pair => pair.Signature, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<DuplicateCandidate>)group
                    .Select(static pair => pair.Candidate)
                    .ToList(),
                StringComparer.Ordinal);

        return strategy.BuildMatches(groups).Any(match =>
            match.Candidates.Any(candidate => string.Equals(
                candidate.File.FullPath,
                keeperCandidate.File.FullPath,
                StringComparison.OrdinalIgnoreCase)) &&
            match.Candidates.Any(candidate => string.Equals(
                candidate.File.FullPath,
                memberCandidate.File.FullPath,
                StringComparison.OrdinalIgnoreCase)));
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

        await _cleanupLogRepository.LogResultAsync(result, suggestion, ct);
    }
}
