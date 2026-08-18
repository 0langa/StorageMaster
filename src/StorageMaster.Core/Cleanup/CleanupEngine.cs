using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup;

/// <summary>
/// Orchestrates all registered ICleanupRule instances and delegates execution
/// to IFileDeleter. Cleanup execution MUST be triggered only after explicit UI confirmation.
/// </summary>
public sealed class CleanupEngine : ICleanupEngine
{
    private readonly IEnumerable<ICleanupRule> _rules;
    private readonly IFileDeleter _deleter;
    private readonly ICleanupLogRepository _log;
    private readonly ILogger<CleanupEngine> _logger;
    private readonly IQuarantineRecorder? _quarantineRecorder;

    public CleanupEngine(
        IEnumerable<ICleanupRule> rules,
        IFileDeleter deleter,
        ICleanupLogRepository log,
        ILogger<CleanupEngine> logger,
        IQuarantineRecorder? quarantineRecorder = null)
    {
        _rules = rules;
        _deleter = deleter;
        _log = log;
        _logger = logger;
        _quarantineRecorder = quarantineRecorder;
    }

    public async IAsyncEnumerable<CleanupSuggestion> GetSuggestionsAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var rule in _rules)
        {
            _logger.LogDebug("Running cleanup rule: {RuleId}", rule.RuleId);
            await using var enumerator = rule
                .AnalyzeAsync(sessionId, settings, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                CleanupSuggestion suggestion;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                    suggestion = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cleanup rule {RuleId} failed; continuing with remaining rules", rule.RuleId);
                    break;
                }

                yield return suggestion;
            }
        }
    }

    public async Task<IReadOnlyList<CleanupResult>> ExecuteAsync(
        IReadOnlyList<CleanupSuggestion> suggestions,
        bool dryRun,
        DeletionMethod deletionMethod,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CleanupResult>(suggestions.Count);

        for (int i = 0; i < suggestions.Count; i++)
        {
            var suggestion = suggestions[i];
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new CleanupProgress(i, suggestions.Count, suggestion.Title));
            _logger.LogInformation("Executing cleanup: {Title} dryRun={DryRun}", suggestion.Title, dryRun);

            var execution = await ExecuteSuggestionAsync(suggestion, dryRun, deletionMethod, cancellationToken);
            var result = execution.Result;
            try
            {
                // Filesystem mutation may already have completed. User
                // cancellation must not suppress its audit record.
                await _log.LogResultAsync(result, suggestion, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup audit write failed after executing {Title}", suggestion.Title);
                result = result with
                {
                    Status = !dryRun && result.Status == CleanupResultStatus.Success
                        ? CleanupResultStatus.PartialSuccess
                        : result.Status,
                    ErrorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? $"Cleanup completed, but audit logging failed: {ex.Message}"
                        : $"{result.ErrorMessage} Audit logging also failed: {ex.Message}",
                };
            }

            results.Add(result);
            if (execution.WasCancelled)
                throw new OperationCanceledException("Cleanup was cancelled after partial results were audited.", cancellationToken);
        }

        // Report 100% completion.
        progress?.Report(new CleanupProgress(suggestions.Count, suggestions.Count, string.Empty));

        return results;
    }

    private async Task<SuggestionExecution> ExecuteSuggestionAsync(
        CleanupSuggestion suggestion,
        bool dryRun,
        DeletionMethod deletionMethod,
        CancellationToken ct)
    {
        if (!dryRun)
        {
            var policyFailure = ValidateDeletionPolicy(suggestion, deletionMethod);
            if (policyFailure is not null)
            {
                return new SuggestionExecution(new CleanupResult
                {
                    SuggestionId = suggestion.Id,
                    Status = CleanupResultStatus.Failed,
                    BytesFreed = 0,
                    ExecutedUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    FailedPaths = suggestion.TargetPaths,
                    ErrorMessage = policyFailure,
                }, WasCancelled: false);
            }
        }

        long totalFreed = 0;
        var failedPaths = new List<string>();
        var quarantinedPaths = new List<QuarantineMove>();
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var successfulPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? firstError = null;
        var cancellationObserved = false;
        var hasPostMutationWarning = false;

        var requests = suggestion.TargetPaths.Select(path =>
        {
            suggestion.ExpectedFileSnapshots.TryGetValue(path, out var expectedSnapshot);
            return new DeletionRequest(
                Path: path,
                Method: deletionMethod,
                DryRun: dryRun,
                ExpectedSnapshot: expectedSnapshot);
        }).ToList();

        try
        {
            await foreach (var outcome in _deleter.DeleteManyAsync(requests, ct))
            {
                processedPaths.Add(outcome.Path);
                if (outcome.Success)
                {
                    successfulPaths.Add(outcome.Path);
                    totalFreed += outcome.BytesFreed;
                    if (outcome.QuarantinePath is not null)
                    {
                        quarantinedPaths.Add(new QuarantineMove(outcome.Path, outcome.QuarantinePath));
                        if (!dryRun)
                        {
                            var warning = await RecordQuarantineRestorePointAsync(
                                outcome.Path,
                                outcome.QuarantinePath);
                            if (warning is not null)
                            {
                                hasPostMutationWarning = true;
                                firstError ??= warning;
                            }
                        }
                    }
                }
                else
                {
                    failedPaths.Add(outcome.Path);
                    firstError ??= outcome.Error;
                    _logger.LogWarning("Delete failed: {Path} — {Error}", outcome.Path, outcome.Error);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancellationObserved = true;
        }
        catch (Exception ex)
        {
            firstError ??= $"Deletion service stopped unexpectedly: {ex.Message}";
            _logger.LogError(ex, "Deletion service failed while executing {Title}", suggestion.Title);
        }

        var unprocessedRequests = requests
            .Where(request => !processedPaths.Contains(request.Path))
            .ToList();
        foreach (var request in unprocessedRequests)
            failedPaths.Add(request.Path);

        var wasCancelled = unprocessedRequests.Count > 0 &&
            (cancellationObserved || ct.IsCancellationRequested);
        if (wasCancelled)
            firstError ??= "Cleanup cancelled before every target was processed.";

        var status = requests.Count switch
        {
            0 => CleanupResultStatus.Skipped,
            _ when failedPaths.Count == 0 && !hasPostMutationWarning => CleanupResultStatus.Success,
            _ when successfulPaths.Count > 0 => CleanupResultStatus.PartialSuccess,
            _ => CleanupResultStatus.Failed,
        };

        return new SuggestionExecution(new CleanupResult
        {
            SuggestionId = suggestion.Id,
            Status = status,
            BytesFreed = totalFreed,
            ExecutedUtc = DateTime.UtcNow,
            WasDryRun = dryRun,
            FailedPaths = failedPaths,
            ErrorMessage = firstError,
            QuarantinedPaths = quarantinedPaths,
        }, wasCancelled);
    }

    /// <summary>
    /// Writes a QuarantinedFiles restore record (MemberId=null, generic-cleanup
    /// run) so the file shows up in the app's quarantine view. A recording
    /// failure must not fail the cleanup — the move already succeeded and the
    /// paths are still captured in the CleanupLog audit JSON.
    /// </summary>
    private async Task<string?> RecordQuarantineRestorePointAsync(
        string originalPath,
        string quarantinePath)
    {
        if (_quarantineRecorder is null)
            return null;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _quarantineRecorder.RecordQuarantineAsync(
                memberId: null,
                IQuarantineRecorder.GenericCleanupRunId,
                originalPath,
                quarantinePath,
                timeout.Token);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Quarantine restore record failed for {Path}; file remains restorable manually from {QuarantinePath}",
                originalPath, quarantinePath);
            return $"File was quarantined, but its restore catalog entry could not be written. " +
                   $"Manual recovery path: {quarantinePath}. Error: {ex.Message}";
        }
    }

    private static string? ValidateDeletionPolicy(CleanupSuggestion suggestion, DeletionMethod deletionMethod)
    {
        if (deletionMethod == DeletionMethod.Permanent &&
            suggestion.Risk == CleanupRisk.High)
        {
            return "High-risk cleanup suggestion cannot be permanent-deleted from the generic cleanup engine. Use Recycle Bin or dry run.";
        }

        if (deletionMethod == DeletionMethod.Permanent && !suggestion.SupportsPermanentDelete)
            return "Cleanup suggestion does not support permanent delete.";

        if (deletionMethod == DeletionMethod.RecycleBin && !suggestion.SupportsRecycleBin)
            return "Cleanup suggestion cannot be sent to the Recycle Bin.";

        if (deletionMethod == DeletionMethod.Quarantine && !suggestion.SupportsQuarantine)
            return "Cleanup suggestion does not support quarantine.";

        return null;
    }

    private sealed record SuggestionExecution(CleanupResult Result, bool WasCancelled);
}
