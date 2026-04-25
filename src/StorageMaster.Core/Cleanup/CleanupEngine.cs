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
    private readonly IEnumerable<ICleanupRule>  _rules;
    private readonly IFileDeleter               _deleter;
    private readonly ICleanupLogRepository      _log;
    private readonly ILogger<CleanupEngine>     _logger;

    public CleanupEngine(
        IEnumerable<ICleanupRule> rules,
        IFileDeleter              deleter,
        ICleanupLogRepository     log,
        ILogger<CleanupEngine>    logger)
    {
        _rules   = rules;
        _deleter = deleter;
        _log     = log;
        _logger  = logger;
    }

    public async IAsyncEnumerable<CleanupSuggestion> GetSuggestionsAsync(
        long              sessionId,
        AppSettings       settings,
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
        bool              dryRun,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CleanupResult>(suggestions.Count);

        foreach (var suggestion in suggestions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Executing cleanup: {Title} dryRun={DryRun}", suggestion.Title, dryRun);

            var result = await ExecuteSuggestionAsync(suggestion, dryRun, cancellationToken);
            results.Add(result);

            await _log.LogResultAsync(result, suggestion, cancellationToken);
        }

        return results;
    }

    private async Task<CleanupResult> ExecuteSuggestionAsync(
        CleanupSuggestion suggestion,
        bool              dryRun,
        CancellationToken ct)
    {
        long totalFreed     = 0;
        var  failedPaths    = new List<string>();
        string? firstError  = null;

        var requests = suggestion.TargetPaths.Select(path => new DeletionRequest(
            Path:   path,
            Method: DeletionMethod.RecycleBin,
            DryRun: dryRun)).ToList();

        await foreach (var outcome in _deleter.DeleteManyAsync(requests, ct))
        {
            if (outcome.Success)
            {
                totalFreed += outcome.BytesFreed;
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
            _ when totalFreed > 0     => CleanupResultStatus.PartialSuccess,
            _ when requests.Count > 0 => CleanupResultStatus.Failed,
            _                         => CleanupResultStatus.Skipped,
        };

        return new CleanupResult
        {
            SuggestionId = suggestion.Id,
            Status       = status,
            BytesFreed   = totalFreed,
            ExecutedUtc  = DateTime.UtcNow,
            WasDryRun    = dryRun,
            FailedPaths  = failedPaths,
            ErrorMessage = firstError,
        };
    }
}
