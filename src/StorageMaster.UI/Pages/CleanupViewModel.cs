using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;

namespace StorageMaster.UI.Pages;

/// <summary>Wraps a CleanupSuggestion with UI selection state.</summary>
public sealed partial class SuggestionItem : ObservableObject
{
    public CleanupSuggestion Suggestion { get; }

    [ObservableProperty] private bool _isSelected = true;

    public string SizeDisplay    => ByteSizeConverter.Format(Suggestion.EstimatedBytes);
    public string RiskDisplay    => Suggestion.Risk.ToString();
    public string CategoryDisplay => Suggestion.Category.ToString();

    public SuggestionItem(CleanupSuggestion suggestion) => Suggestion = suggestion;
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupEngine    _engine;
    private readonly IScanRepository   _repo;
    private readonly ISettingsRepository _settings;

    [ObservableProperty] private bool        _isLoading;
    [ObservableProperty] private bool        _isExecuting;
    [ObservableProperty] private bool        _isDryRun           = false;
    [ObservableProperty] private string      _statusMessage      = "Select a scan session and analyse to see suggestions.";
    [ObservableProperty] private ScanSession? _selectedSession;
    [ObservableProperty] private string      _totalSelectedSize  = "0 B";
    [ObservableProperty] private bool        _hasResults;
    [ObservableProperty] private bool        _hasExecutionResults;

    partial void OnSelectedSessionChanged(ScanSession? value) =>
        OnPropertyChanged(nameof(CanAnalyse));

    public bool CanAnalyse => SelectedSession is not null && !IsLoading;

    public ObservableCollection<SuggestionItem>       Suggestions     { get; } = [];
    public ObservableCollection<ScanSession>          RecentSessions  { get; } = [];
    public ObservableCollection<CleanupResultDisplay> ExecutionResults { get; } = [];

    public CleanupViewModel(
        ICleanupEngine     engine,
        IScanRepository    repo,
        ISettingsRepository settings)
    {
        _engine   = engine;
        _repo     = repo;
        _settings = settings;
    }

    public async Task InitializeAsync()
    {
        var sessions = await _repo.GetRecentSessionsAsync(10);
        RecentSessions.Clear();
        foreach (var s in sessions.Where(s => s.Status == ScanStatus.Completed))
            RecentSessions.Add(s);

        if (RecentSessions.Count > 0)
            SelectedSession = RecentSessions[0];
    }

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        if (SelectedSession is null) return;

        IsLoading            = true;
        HasResults           = false;
        HasExecutionResults  = false;
        Suggestions.Clear();
        ExecutionResults.Clear();
        StatusMessage = "Analysing…";

        try
        {
            var settings = await _settings.LoadAsync();
            await foreach (var suggestion in _engine.GetSuggestionsAsync(SelectedSession.Id, settings))
            {
                Suggestions.Add(new SuggestionItem(suggestion));
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

    /// <summary>
    /// Called from the UI's confirmation dialog — never invoke without user confirmation.
    /// </summary>
    [RelayCommand]
    private async Task ExecuteCleanupAsync()
    {
        var selected = Suggestions
            .Where(s => s.IsSelected)
            .Select(s => s.Suggestion)
            .ToList();

        if (selected.Count == 0) return;

        IsExecuting         = true;
        HasExecutionResults = false;
        ExecutionResults.Clear();
        StatusMessage = IsDryRun ? "Running dry-run…" : "Cleaning up…";

        try
        {
            var settings = await _settings.LoadAsync();
            var results  = await _engine.ExecuteAsync(selected, IsDryRun);

            foreach (var r in results)
            {
                ExecutionResults.Add(new CleanupResultDisplay(
                    selected.First(s => s.Id == r.SuggestionId).Title,
                    r.Status.ToString(),
                    ByteSizeConverter.Format(r.BytesFreed),
                    r.WasDryRun,
                    r.ErrorMessage));
            }
            HasExecutionResults = ExecutionResults.Count > 0;

            long totalFreed = results.Sum(r => r.BytesFreed);
            StatusMessage = IsDryRun
                ? $"Dry run complete. Would free {ByteSizeConverter.Format(totalFreed)}."
                : $"Cleanup complete. Freed {ByteSizeConverter.Format(totalFreed)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void UpdateTotalSelected()
    {
        long total = Suggestions
            .Where(s => s.IsSelected)
            .Sum(s => s.Suggestion.EstimatedBytes);
        TotalSelectedSize = ByteSizeConverter.Format(total);
    }
}

public sealed record CleanupResultDisplay(
    string Title,
    string Status,
    string BytesFreed,
    bool   WasDryRun,
    string? Error);
