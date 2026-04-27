using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;

namespace StorageMaster.UI.Pages;

/// <summary>Wraps a CleanupSuggestion with UI selection state.</summary>
public sealed partial class SuggestionItem : ObservableObject
{
    public CleanupSuggestion Suggestion { get; }

    [ObservableProperty] private bool _isSelected = true;

    public string SizeDisplay     => ByteSizeConverter.Format(Suggestion.EstimatedBytes);
    public string RiskDisplay     => Suggestion.Risk.ToString();
    public string CategoryDisplay => Suggestion.Category.ToString();

    public SuggestionItem(CleanupSuggestion suggestion) => Suggestion = suggestion;
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupEngine     _engine;
    private readonly IScanRepository    _repo;
    private readonly ISettingsRepository _settings;
    private readonly DispatcherQueue    _dispatcherQueue;

    // ── Analysis state ──────────────────────────────────────────────────────
    [ObservableProperty] private bool        _isLoading;
    [ObservableProperty] private bool        _isDryRun          = false;
    [ObservableProperty] private string      _statusMessage     = "Select a scan session and analyse to see suggestions.";
    [ObservableProperty] private ScanSession? _selectedSession;
    [ObservableProperty] private string      _totalSelectedSize = "0 B";
    [ObservableProperty] private bool        _hasResults;

    // ── Execution state ─────────────────────────────────────────────────────
    [ObservableProperty] private bool        _isExecuting;
    [ObservableProperty] private string      _cleanupProgressText  = string.Empty;
    [ObservableProperty] private double      _cleanupProgressValue;
    [ObservableProperty] private bool        _hasExecutionResults;

    // ── Last-run metadata (read by code-behind to build report dialog) ───────
    [ObservableProperty] private bool          _lastRunWasDryRun;
    [ObservableProperty] private DeletionMethod _lastRunDeletionMethod;
    [ObservableProperty] private string        _lastRunSummary   = string.Empty;

    partial void OnSelectedSessionChanged(ScanSession? value) =>
        OnPropertyChanged(nameof(CanAnalyse));
    partial void OnIsLoadingChanged(bool value)   => OnPropertyChanged(nameof(CanAnalyse));
    partial void OnIsExecutingChanged(bool value) => OnPropertyChanged(nameof(CanAnalyse));

    public bool CanAnalyse => SelectedSession is not null && !IsLoading && !IsExecuting;

    public ObservableCollection<SuggestionItem>       Suggestions     { get; } = [];
    public ObservableCollection<ScanSession>          RecentSessions  { get; } = [];
    public ObservableCollection<CleanupResultDisplay> ExecutionResults { get; } = [];

    // Stored between initial run and any follow-up re-runs.
    private IReadOnlyList<CleanupSuggestion> _lastSelectedSuggestions = [];

    public CleanupViewModel(
        ICleanupEngine      engine,
        IScanRepository     repo,
        ISettingsRepository settings)
    {
        _engine          = engine;
        _repo            = repo;
        _settings        = settings;
        // Must be captured on the UI thread (constructor is called from CleanupPage ctor).
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public async Task InitializeAsync()
    {
        var s = await _settings.LoadAsync();
        IsDryRun = s.DryRunByDefault;

        var sessions = await _repo.GetRecentSessionsAsync(10);
        RecentSessions.Clear();
        foreach (var session in sessions.Where(s => s.Status == ScanStatus.Completed))
            RecentSessions.Add(session);

        if (RecentSessions.Count > 0 && SelectedSession is null)
            SelectedSession = RecentSessions[0];
    }

    // ── Analysis ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        if (SelectedSession is null) return;

        IsLoading           = true;
        HasResults          = false;
        HasExecutionResults = false;
        Suggestions.Clear();
        ExecutionResults.Clear();
        _lastSelectedSuggestions = [];
        StatusMessage = "Analysing…";

        try
        {
            var settings = await _settings.LoadAsync();
            await foreach (var suggestion in _engine.GetSuggestionsAsync(SelectedSession.Id, settings))
            {
                var item = new SuggestionItem(suggestion);
                item.PropertyChanged += SuggestionItem_PropertyChanged;
                Suggestions.Add(item);
            }
            UpdateTotalSelected();
            HasResults    = Suggestions.Count > 0;
            StatusMessage = Suggestions.Count > 0
                ? $"Found {Suggestions.Count} suggestion(s). Select items to clean up."
                : "No cleanup opportunities found for this scan.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analysis failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Initial cleanup run. Reads IsDryRun and PreferRecycleBin from the current
    /// state / settings. Called only after explicit user confirmation in the UI.
    /// </summary>
    [RelayCommand]
    private async Task ExecuteCleanupAsync()
    {
        var selected = Suggestions
            .Where(s => s.IsSelected)
            .Select(s => s.Suggestion)
            .ToList();

        if (selected.Count == 0) return;

        _lastSelectedSuggestions = selected;

        var settings = await _settings.LoadAsync();
        var method = settings.PreferRecycleBin
            ? DeletionMethod.RecycleBin
            : DeletionMethod.Permanent;

        await RunCleanupCoreAsync(IsDryRun, method, selected);
    }

    /// <summary>
    /// Re-run the same items with a different mode (called from the report dialog).
    /// </summary>
    public Task RunCleanupWithMethodAsync(bool dryRun, DeletionMethod method)
    {
        if (_lastSelectedSuggestions.Count == 0) return Task.CompletedTask;
        return RunCleanupCoreAsync(dryRun, method, _lastSelectedSuggestions);
    }

    private async Task RunCleanupCoreAsync(
        bool                            dryRun,
        DeletionMethod                  method,
        IReadOnlyList<CleanupSuggestion> suggestions)
    {
        IsExecuting          = true;
        LastRunWasDryRun     = dryRun;
        LastRunDeletionMethod = method;
        HasExecutionResults  = false;
        ExecutionResults.Clear();
        CleanupProgressValue = 0;
        CleanupProgressText  = dryRun ? "Running dry-run preview…" : "Cleaning up…";
        StatusMessage        = CleanupProgressText;

        var dq = _dispatcherQueue;
        var progress = new Progress<CleanupProgress>(p =>
        {
            void Apply()
            {
                CleanupProgressValue = p.Total > 0
                    ? (double)p.Completed / p.Total * 100.0
                    : 0;
                CleanupProgressText = p.Completed < p.Total
                    ? $"Item {p.Completed + 1} of {p.Total}: {p.CurrentTitle}"
                    : dryRun ? "Preview complete." : "Cleanup complete.";
            }
            if (dq.HasThreadAccess) Apply();
            else dq.TryEnqueue(Apply);
        });

        try
        {
            var results = await _engine.ExecuteAsync(suggestions, dryRun, method, progress);

            foreach (var r in results)
            {
                ExecutionResults.Add(new CleanupResultDisplay(
                    suggestions.First(s => s.Id == r.SuggestionId).Title,
                    r.Status.ToString(),
                    ByteSizeConverter.Format(r.BytesFreed),
                    r.WasDryRun,
                    r.ErrorMessage));
            }
            HasExecutionResults = ExecutionResults.Count > 0;

            long totalFreed = results.Sum(r => r.BytesFreed);
            int  succeeded  = results.Count(r => r.Status is CleanupResultStatus.Success
                                                            or CleanupResultStatus.PartialSuccess);
            int  failed     = results.Count(r => r.Status == CleanupResultStatus.Failed);
            int  skipped    = results.Count(r => r.Status == CleanupResultStatus.Skipped);

            LastRunSummary = dryRun
                ? $"Preview: would free {ByteSizeConverter.Format(totalFreed)} across {succeeded} item(s)."
                : BuildSummaryText(totalFreed, succeeded, failed, skipped, method);

            StatusMessage = LastRunSummary;
        }
        catch (Exception ex)
        {
            LastRunSummary = $"Cleanup failed: {ex.Message}";
            StatusMessage  = LastRunSummary;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private static string BuildSummaryText(long freed, int succeeded, int failed, int skipped, DeletionMethod method)
    {
        var how = method == DeletionMethod.RecycleBin ? "to the Recycle Bin" : "permanently";
        var sb  = new System.Text.StringBuilder();
        sb.Append($"Freed {ByteSizeConverter.Format(freed)} {how}");
        if (succeeded > 0) sb.Append($" ({succeeded} succeeded");
        if (failed    > 0) sb.Append($", {failed} failed");
        if (skipped   > 0) sb.Append($", {skipped} skipped");
        if (succeeded > 0 || failed > 0 || skipped > 0) sb.Append(')');
        sb.Append('.');
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void UpdateTotalSelected()
    {
        long total = Suggestions
            .Where(s => s.IsSelected)
            .Sum(s => s.Suggestion.EstimatedBytes);
        TotalSelectedSize = ByteSizeConverter.Format(total);
    }

    private void SuggestionItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SuggestionItem.IsSelected))
            UpdateTotalSelected();
    }
}

public sealed record CleanupResultDisplay(
    string  Title,
    string  Status,
    string  BytesFreed,
    bool    WasDryRun,
    string? Error);
