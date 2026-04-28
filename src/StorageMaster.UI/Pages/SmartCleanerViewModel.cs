using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;

namespace StorageMaster.UI.Pages;

/// <summary>
/// Wraps a SmartCleanGroup with UI state (selected toggle, formatted size).
/// </summary>
public sealed partial class SmartCleanGroupItem : ObservableObject
{
    public SmartCleanGroup Group { get; }

    [ObservableProperty] private bool _isSelected = true;

    public string Category      => Group.Category;
    public string Description   => Group.Description;
    public string IconGlyph     => Group.IconGlyph;
    public string SizeDisplay   => ByteSizeConverter.Format(Group.EstimatedBytes);

    public SmartCleanGroupItem(SmartCleanGroup group) => Group = group;
}

public sealed partial class SmartCleanerViewModel : ObservableObject
{
    private readonly ISmartCleanerService _service;
    private readonly ISettingsRepository  _settings;
    private readonly DispatcherQueue      _dispatcherQueue;

    // ── State ───────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isScanning;
    [ObservableProperty] private bool   _isCleaning;
    [ObservableProperty] private bool   _hasResults;
    [ObservableProperty] private bool   _cleaningDone;
    [ObservableProperty] private string _statusText     = "Click \"Scan & Analyse\" to find junk files automatically.";
    [ObservableProperty] private string _progressText   = string.Empty;
    [ObservableProperty] private string _totalSizeText  = string.Empty;
    [ObservableProperty] private string _freedText      = string.Empty;
    [ObservableProperty] private bool   _useRecycleBin  = true;

    public bool CanClean => HasResults && !IsScanning && !IsCleaning;

    partial void OnHasResultsChanged(bool value)  => OnPropertyChanged(nameof(CanClean));
    partial void OnIsScanningChanged(bool value)  => OnPropertyChanged(nameof(CanClean));
    partial void OnIsCleaningChanged(bool value)  => OnPropertyChanged(nameof(CanClean));

    public ObservableCollection<SmartCleanGroupItem> Groups { get; } = [];

    private IReadOnlyList<SmartCleanGroup> _lastGroups = [];

    public SmartCleanerViewModel(ISmartCleanerService service, ISettingsRepository settings)
    {
        _service         = service;
        _settings        = settings;
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
        IsScanning  = true;
        HasResults  = false;
        CleaningDone = false;
        FreedText   = string.Empty;
        Groups.Clear();
        StatusText  = "Scanning your PC for junk files…";

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
                Groups.Add(new SmartCleanGroupItem(g));

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

        IsCleaning  = true;
        CleaningDone = false;
        FreedText   = string.Empty;
        StatusText  = "Cleaning…";

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

            FreedText    = ByteSizeConverter.Format(freed);
            StatusText   = $"Done! Freed {FreedText} of disk space.";
            CleaningDone = true;
            HasResults   = false;
            Groups.Clear();
        }
        catch (Exception ex)
        {
            StatusText = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            IsCleaning   = false;
            ProgressText = string.Empty;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void UpdateTotalSize()
    {
        long total = Groups.Where(g => g.IsSelected).Sum(g => g.Group.EstimatedBytes);
        TotalSizeText = ByteSizeConverter.Format(total);
    }
}
