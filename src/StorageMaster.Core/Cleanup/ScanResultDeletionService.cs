using System.Text.Json;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup;

public sealed class ScanResultDeletionService(
    IFileDeleter deleter,
    ICleanupLogRepository cleanupLogRepository,
    IScanRepository scanRepository) : IScanResultDeletionService
{
    public async Task<DeletionOutcome> DeleteAsync(
        FileEntry file,
        DeletionMethod method,
        CancellationToken ct = default)
    {
        var outcome = await deleter.DeleteAsync(
            new DeletionRequest(file.FullPath, method, DryRun: false),
            ct);

        if (!outcome.Success)
            return outcome;

        await scanRepository.DeleteFileEntryAsync(file.Id, ct);
        await scanRepository.MarkSessionStaleAsync(
            file.SessionId,
            $"Session modified after deleting {file.FullPath} on {DateTime.Now:g}. Re-run scan for exact totals.",
            ct);

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

        await cleanupLogRepository.LogResultAsync(result, suggestion, ct);
        return outcome;
    }
}
