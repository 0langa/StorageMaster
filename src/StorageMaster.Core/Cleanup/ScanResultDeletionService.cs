using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup;

public sealed class ScanResultDeletionService(
    IFileDeleter deleter,
    ICleanupLogRepository cleanupLogRepository,
    IScanRepository scanRepository,
    ILogger<ScanResultDeletionService>? logger = null) : IScanResultDeletionService
{
    private readonly ILogger<ScanResultDeletionService> _logger =
        logger ?? NullLogger<ScanResultDeletionService>.Instance;

    public async Task<DeletionOutcome> DeleteAsync(
        FileEntry file,
        DeletionMethod method,
        CancellationToken ct = default)
    {
        if (file.Identity is null)
        {
            return new DeletionOutcome(
                file.FullPath,
                false,
                0,
                "Stored scan result has no stable file identity. Re-run the scan before deleting it.");
        }

        var outcome = await deleter.DeleteAsync(
            new DeletionRequest(
                file.FullPath,
                method,
                DryRun: false,
                ExpectedSnapshot: new FileSnapshot(
                    file.FullPath,
                    file.Identity,
                    file.SizeBytes,
                    file.ModifiedUtc,
                    file.Attributes)),
            ct);

        if (!outcome.Success)
            return outcome;

        var suggestion = new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = "results.delete",
            Title = $"Results deletion: {file.FileName}",
            Description = "File deleted directly from a results session.",
            Category = CleanupCategory.Custom,
            Risk = CleanupRisk.Low,
            EstimatedBytes = file.SizeBytes,
            TargetPaths = [file.FullPath],
            IsSystemPath = false,
            AuditDataJson = JsonSerializer.Serialize(new
            {
                Source = "ResultsPage",
                file.SessionId,
                FileEntryId = file.Id,
                file.FullPath,
                Method = method.ToString(),
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

        var bookkeepingWarnings = new List<string>();
        await TryBookkeepingAsync(
            () => cleanupLogRepository.LogResultAsync(result, suggestion, CancellationToken.None),
            "write deletion audit",
            bookkeepingWarnings);
        await TryBookkeepingAsync(
            () => scanRepository.DeleteFileEntryAsync(file.Id, CancellationToken.None),
            "remove stale scan entry",
            bookkeepingWarnings);
        await TryBookkeepingAsync(
            () => scanRepository.MarkSessionStaleAsync(
                file.SessionId,
                $"Session modified after deleting {file.FullPath} on {DateTime.Now:g}. Re-run scan for exact totals.",
                CancellationToken.None),
            "mark scan session stale",
            bookkeepingWarnings);

        return bookkeepingWarnings.Count == 0
            ? outcome
            : outcome with
            {
                Error = "File was deleted, but StorageMaster could not " +
                    string.Join("; ", bookkeepingWarnings) + ". Re-run the scan.",
            };
    }

    private async Task TryBookkeepingAsync(
        Func<Task> operation,
        string description,
        ICollection<string> warnings)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            warnings.Add(description);
            _logger.LogError(ex, "Post-deletion bookkeeping failed: {Description}", description);
        }
    }
}
