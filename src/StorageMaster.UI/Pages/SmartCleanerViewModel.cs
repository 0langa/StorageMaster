using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

/// <summary>
/// Wraps a SmartCleanGroup with UI state (selected toggle, formatted size).
/// </summary>
public sealed partial class SmartCleanGroupItem : ObservableObject
{
    public SmartCleanGroup Group { get; }

    [ObservableProperty] private bool _isSelected = true;

    public string Category => Group.Category;
    public string Description => Group.Description;
    public string IconGlyph => Group.IconGlyph;
    public string SizeDisplay => ByteSizeConverter.Format(Group.EstimatedBytes);

    public SmartCleanGroupItem(SmartCleanGroup group) => Group = group;
}

public sealed partial class SmartCleanerViewModel : ObservableObject
{
    private readonly ISmartCleanerService _service;
    private readonly ISettingsRepository _settings;
    private readonly IDialogService _dialogs;
    private readonly DispatcherQueue _dispatcherQueue;

    // ── State ───────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _cleaningDone;
    [ObservableProperty] private string _statusText = "Click \"Scan & Analyse\" to find junk files automatically.";
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _totalSizeText = string.Empty;
    [ObservableProperty] private string _freedText = string.Empty;
    [ObservableProperty] private bool _useRecycleBin = true;
    [ObservableProperty] private int _selectedGroupCount;

    public bool CanClean => HasResults && !IsScanning && !IsCleaning && SelectedGroupCount > 0;

    partial void OnHasResultsChanged(bool value) => OnPropertyChanged(nameof(CanClean));
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanClean));
    partial void OnIsCleaningChanged(bool value) => OnPropertyChanged(nameof(CanClean));
    partial void OnSelectedGroupCountChanged(int value) => OnPropertyChanged(nameof(CanClean));

    public ObservableCollection<SmartCleanGroupItem> Groups { get; } = [];

    private IReadOnlyList<SmartCleanGroup> _lastGroups = [];

    public SmartCleanerViewModel(
        ISmartCleanerService service,
        ISettingsRepository settings,
        IDialogService dialogs)
    {
        _service = service;
        _settings = settings;
        _dialogs = dialogs;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public async Task InitializeAsync()
    {
        var s = await _settings.LoadAsync();
        UseRecycleBin = s.PreferRecycleBin;
    }

    // ── Analyse ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        IsScanning = true;
        HasResults = false;
        CleaningDone = false;
        FreedText = string.Empty;
        Groups.Clear();
        StatusText = "Scanning your PC for junk files…";

        var dq = _dispatcherQueue;
        var progress = new Progress<string>(msg =>
        {
            void Apply() => ProgressText = msg;
            if (dq.HasThreadAccess) Apply(); else dq.TryEnqueue(Apply);
        });

        try
        {
            var groups = await _service.AnalyzeAsync(progress);
            _lastGroups = groups;

            foreach (var g in groups)
            {
                var item = new SmartCleanGroupItem(g);
                item.PropertyChanged += OnGroupItemPropertyChanged;
                Groups.Add(item);
            }

            UpdateTotalSize();
            HasResults = Groups.Count > 0;
            StatusText = Groups.Count > 0
                ? $"Found {Groups.Count} category/categories of junk. Select what to remove."
                : "Great news — no significant junk found on this PC!";
            ProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ── Clean ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CleanAsync()
    {
        var selected = Groups
            .Where(g => g.IsSelected)
            .Select(g => g.Group)
            .ToList();

        if (selected.Count == 0) return;

        var totalBytes = selected.Sum(static group => group.EstimatedBytes);
        var confirmed = await _dialogs.ConfirmAsync(
            "Confirm cleanup",
            $"Remove {selected.Count} junk group(s) and reclaim about {ByteSizeConverter.Format(totalBytes)}?\n\nFiles will be moved using {(UseRecycleBin ? "Recycle Bin" : "permanent deletion")}.",
            UseRecycleBin ? "Clean selected" : "Delete selected");

        if (!confirmed)
            return;

        IsCleaning = true;
        CleaningDone = false;
        FreedText = string.Empty;
        StatusText = "Cleaning…";

        var dq = _dispatcherQueue;
        var progress = new Progress<string>(msg =>
        {
            void Apply() => ProgressText = msg;
            if (dq.HasThreadAccess) Apply(); else dq.TryEnqueue(Apply);
        });

        try
        {
            var method = UseRecycleBin ? DeletionMethod.RecycleBin : DeletionMethod.Permanent;
            long freed = await _service.CleanAsync(selected, method, progress);

            FreedText = ByteSizeConverter.Format(freed);
            StatusText = $"Done! Freed {FreedText} of disk space.";
            CleaningDone = true;
            HasResults = false;
            SelectedGroupCount = 0;
            UnsubscribeGroups();
            Groups.Clear();
        }
        catch (Exception ex)
        {
            StatusText = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            IsCleaning = false;
            ProgressText = string.Empty;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void UpdateTotalSize()
    {
        long total = Groups.Where(g => g.IsSelected).Sum(g => g.Group.EstimatedBytes);
        TotalSizeText = ByteSizeConverter.Format(total);
        SelectedGroupCount = Groups.Count(g => g.IsSelected);
    }

    private void OnGroupItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SmartCleanGroupItem.IsSelected))
            UpdateTotalSize();
    }

    private void UnsubscribeGroups()
    {
        foreach (var group in Groups)
            group.PropertyChanged -= OnGroupItemPropertyChanged;
    }
}
