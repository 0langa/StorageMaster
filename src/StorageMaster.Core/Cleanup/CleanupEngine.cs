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
            await foreach (var suggestion in rule.AnalyzeAsync(sessionId, settings, cancellationToken))
            {
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

            var result = await ExecuteSuggestionAsync(suggestion, dryRun, deletionMethod, cancellationToken);
            results.Add(result);

            await _log.LogResultAsync(result, suggestion, cancellationToken);
        }

        // Report 100% completion.
        progress?.Report(new CleanupProgress(suggestions.Count, suggestions.Count, string.Empty));

        return results;
    }

    private async Task<CleanupResult> ExecuteSuggestionAsync(
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
                return new CleanupResult
                {
                    SuggestionId = suggestion.Id,
                    Status = CleanupResultStatus.Failed,
                    BytesFreed = 0,
                    ExecutedUtc = DateTime.UtcNow,
                    WasDryRun = false,
                    FailedPaths = suggestion.TargetPaths,
                    ErrorMessage = policyFailure,
                };
            }
        }

        long totalFreed = 0;
        var failedPaths = new List<string>();
        var quarantinedPaths = new List<QuarantineMove>();
        string? firstError = null;

        var requests = suggestion.TargetPaths.Select(path => new DeletionRequest(
            Path: path,
            Method: deletionMethod,
            DryRun: dryRun)).ToList();

        await foreach (var outcome in _deleter.DeleteManyAsync(requests, ct))
        {
            if (outcome.Success)
            {
                totalFreed += outcome.BytesFreed;
                if (outcome.QuarantinePath is not null)
                {
                    quarantinedPaths.Add(new QuarantineMove(outcome.Path, outcome.QuarantinePath));
                    if (!dryRun)
                        await RecordQuarantineRestorePointAsync(outcome.Path, outcome.QuarantinePath, ct);
                }
            }
            else
            {
                failedPaths.Add(outcome.Path);
                firstError ??= outcome.Error;
                _logger.LogWarning("Delete failed: {Path} — {Error}", outcome.Path, outcome.Error);
            }
        }

        var status = failedPaths.Count switch
        {
            0 when requests.Count > 0 => CleanupResultStatus.Success,
            _ when totalFreed > 0 => CleanupResultStatus.PartialSuccess,
            _ when requests.Count > 0 => CleanupResultStatus.Failed,
            _ => CleanupResultStatus.Skipped,
        };

        return new CleanupResult
        {
            SuggestionId = suggestion.Id,
            Status = status,
            BytesFreed = totalFreed,
            ExecutedUtc = DateTime.UtcNow,
            WasDryRun = dryRun,
            FailedPaths = failedPaths,
            ErrorMessage = firstError,
            QuarantinedPaths = quarantinedPaths,
        };
    }

    /// <summary>
    /// Writes a QuarantinedFiles restore record (MemberId=null, generic-cleanup
    /// run) so the file shows up in the app's quarantine view. A recording
    /// failure must not fail the cleanup — the move already succeeded and the
    /// paths are still captured in the CleanupLog audit JSON.
    /// </summary>
    private async Task RecordQuarantineRestorePointAsync(string originalPath, string quarantinePath, CancellationToken ct)
    {
        if (_quarantineRecorder is null)
            return;

        try
        {
            await _quarantineRecorder.RecordQuarantineAsync(
                memberId: null,
                IQuarantineRecorder.GenericCleanupRunId,
                originalPath,
                quarantinePath,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Quarantine restore record failed for {Path}; file remains restorable manually from {QuarantinePath}",
                originalPath, quarantinePath);
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

        if (deletionMethod == DeletionMethod.Quarantine && !suggestion.SupportsQuarantine)
            return "Cleanup suggestion does not support quarantine.";

        return null;
    }
}
