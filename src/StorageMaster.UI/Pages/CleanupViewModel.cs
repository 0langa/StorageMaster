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

/// <summary>
/// Per-category toggle item shown inside an expandable group.
/// </summary>
public sealed partial class CleanupCategoryOption : ObservableObject
{
    public CleanupCategory Category    { get; init; }
    public string          DisplayName { get; init; } = string.Empty;
    public string          Description { get; init; } = string.Empty;
    public string          IconGlyph   { get; init; } = string.Empty;

    [ObservableProperty] private bool _isEnabled = true;
}

/// <summary>
/// A named group of <see cref="CleanupCategoryOption"/> items.
///
/// The group-level toggle cascades to all items (selecting/deselecting all).
/// When individual items are toggled, the group toggle reflects whether all
/// items are currently enabled.
///
/// A <c>_suppressCascade</c> guard prevents infinite loops when refreshing
/// the group state in response to individual item changes.
/// </summary>
public sealed partial class CleanupCategoryGroup : ObservableObject
{
    public string GroupName { get; init; } = string.Empty;
    public string GroupIcon { get; init; } = string.Empty;   // Segoe MDL2 Assets glyph char

    public ObservableCollection<CleanupCategoryOption> Items { get; } = [];

    [ObservableProperty] private bool _isExpanded    = true;
    [ObservableProperty] private bool _isGroupEnabled = true;

    private bool _suppressCascade;

    /// <summary>
    /// Cascade group toggle to all items (unless suppressed during a refresh).
    /// </summary>
    partial void OnIsGroupEnabledChanged(bool value)
    {
        if (_suppressCascade) return;
        foreach (var item in Items)
            item.IsEnabled = value;
    }

    /// <summary>
    /// Called when an individual item's IsEnabled changes. Updates the group
    /// toggle to reflect whether all items are enabled, without triggering the
    /// cascade back to the items.
    /// </summary>
    public void RefreshGroupEnabled()
    {
        _suppressCascade = true;
        IsGroupEnabled   = Items.Count > 0 && Items.All(i => i.IsEnabled);
        _suppressCascade = false;
    }
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

    // ── Per-session cleanup options ──────────────────────────────────────────
    [ObservableProperty] private bool _useRecycleBin       = true;
    [ObservableProperty] private bool _clearEntireDownloads = false;
    [ObservableProperty] private int  _largeFileSizeMb     = 500;
    [ObservableProperty] private int  _oldFileAgeDays      = 365;

    partial void OnLargeFileSizeMbChanged(int value) => OnPropertyChanged(nameof(LargeFileSizeLabel));
    partial void OnOldFileAgeDaysChanged(int value)  => OnPropertyChanged(nameof(OldFileAgeLabel));
    public string LargeFileSizeLabel => $"Large file threshold: {LargeFileSizeMb:N0} MB";
    public string OldFileAgeLabel    => $"Old file age: {OldFileAgeDays:N0} days";

    partial void OnSelectedSessionChanged(ScanSession? value) =>
        OnPropertyChanged(nameof(CanAnalyse));
    partial void OnIsLoadingChanged(bool value)   => OnPropertyChanged(nameof(CanAnalyse));
    partial void OnIsExecutingChanged(bool value) => OnPropertyChanged(nameof(CanAnalyse));

    public bool CanAnalyse => SelectedSession is not null && !IsLoading && !IsExecuting;

    /// <summary>
    /// Computed property for the Large &amp; Old Files slider visibility.
    /// Replaces the brittle CategoryOptions[9] index binding.
    /// </summary>
    public bool IsLargeOldFilesEnabled =>
        CategoryGroups
            .SelectMany(g => g.Items)
            .FirstOrDefault(o => o.Category == CleanupCategory.LargeOldFiles)
            ?.IsEnabled ?? false;

    public ObservableCollection<SuggestionItem>       Suggestions     { get; } = [];
    public ObservableCollection<ScanSession>          RecentSessions  { get; } = [];
    public ObservableCollection<CleanupResultDisplay> ExecutionResults { get; } = [];

    /// <summary>
    /// Grouped category options. Each group has a header toggle and an
    /// expandable list of individual <see cref="CleanupCategoryOption"/> items.
    /// </summary>
    public ObservableCollection<CleanupCategoryGroup> CategoryGroups { get; } = [];

    /// <summary>
    /// Flat view of all category options across all groups.
    /// Used by <see cref="AnalyseAsync"/> to filter suggestions — no changes
    /// needed there despite the internal restructuring.
    /// </summary>
    public IEnumerable<CleanupCategoryOption> CategoryOptions =>
        CategoryGroups.SelectMany(g => g.Items);

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
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        BuildCategoryGroups();
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    private void BuildCategoryGroups()
    {
        CategoryGroups.Clear();

        // ── Windows System ───────────────────────────────────────────────────
        var winSystem = MakeGroup("Windows System", "");
        AddItem(winSystem, CleanupCategory.RecycleBin,            "Recycle Bin",           "Files sitting in the Windows Recycle Bin.",                                      enabled: true);
        AddItem(winSystem, CleanupCategory.TempFiles,             "Temporary Files",        "Windows Temp folders and .tmp/.temp files.",                                     enabled: true);
        AddItem(winSystem, CleanupCategory.WindowsUpdateCache,    "Windows Update Cache",   "Applied update packages in SoftwareDistribution\\Download.",                     enabled: true);
        AddItem(winSystem, CleanupCategory.DeliveryOptimization,  "Delivery Optimisation",  "Peer-to-peer Windows Update sharing cache.",                                     enabled: true);
        AddItem(winSystem, CleanupCategory.WindowsErrorReporting, "Error Reports & Dumps",  "WER crash logs and memory dumps.",                                               enabled: true);
        AddItem(winSystem, CleanupCategory.ThumbnailCache,        "Thumbnail Cache",         "Explorer thumbnail database files. Rebuilt on demand.",                          enabled: true);
        AddItem(winSystem, CleanupCategory.IconCache,             "Icon Cache",              "Explorer icon cache database files. Rebuilt automatically.",                     enabled: true);
        AddItem(winSystem, CleanupCategory.FontCache,             "Font Cache",              "Font rendering cache. Rebuilt on next boot.",                                    enabled: false);
        AddItem(winSystem, CleanupCategory.PrefetchFiles,         "Prefetch Files",          "App launch prefetch data. Slight slowdown after clean (requires elevation).",    enabled: false);
        AddItem(winSystem, CleanupCategory.DnsCache,              "DNS Client Cache",        "Flushes the DNS resolver cache (runs ipconfig /flushdns).",                      enabled: true);

        // ── Browsers ─────────────────────────────────────────────────────────
        var browsers = MakeGroup("Browsers", "");
        AddItem(browsers, CleanupCategory.BrowserCache, "Browser Cache", "Chrome, Edge, Firefox, Brave, and Opera cache directories.", enabled: true);

        // ── Applications ─────────────────────────────────────────────────────
        var apps = MakeGroup("Applications", "");
        AddItem(apps, CleanupCategory.CacheFolders,     "Application Caches",   "AppData cache folders left by various apps.",                    enabled: true);
        AddItem(apps, CleanupCategory.ProgramLeftovers, "Program Leftovers",    "AppData folders from uninstalled programs (90+ days inactive).", enabled: true);
        AddItem(apps, CleanupCategory.StoreLogs,        "Microsoft Store Logs", "Diagnostic logs from the Microsoft Store.",                      enabled: true);

        // ── Downloads & Installers ────────────────────────────────────────────
        var downloads = MakeGroup("Downloads & Installers", "");
        AddItem(downloads, CleanupCategory.DownloadedInstallers, "Downloaded Installers", "Installer files (.exe, .msi, .iso …) in your Downloads folder.", enabled: true);

        // ── Files & Storage ───────────────────────────────────────────────────
        var files = MakeGroup("Files & Storage", "");
        AddItem(files, CleanupCategory.LargeOldFiles, "Large & Old Files", "Files above the size threshold not modified within the age threshold.", enabled: false);

        CategoryGroups.Add(winSystem);
        CategoryGroups.Add(browsers);
        CategoryGroups.Add(apps);
        CategoryGroups.Add(downloads);
        CategoryGroups.Add(files);
    }

    private static CleanupCategoryGroup MakeGroup(string name, string icon) =>
        new() { GroupName = name, GroupIcon = icon };

    private static void AddItem(
        CleanupCategoryGroup group,
        CleanupCategory      category,
        string               displayName,
        string               description,
        bool                 enabled)
    {
        var item = new CleanupCategoryOption
        {
            Category    = category,
            DisplayName = displayName,
            Description = description,
            IconGlyph   = string.Empty,
            IsEnabled   = enabled,
        };

        // Keep group toggle in sync when individual items change.
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CleanupCategoryOption.IsEnabled)) return;
            group.RefreshGroupEnabled();

            // The LargeOldFiles slider visibility is bound to IsLargeOldFilesEnabled.
            // We can't raise it from here without a back-reference to the ViewModel,
            // so the XAML binds via ViewModel.IsLargeOldFilesEnabled with Mode=OneWay
            // and we raise it in SetCategory (called from InitializeAsync).
        };

        group.Items.Add(item);
        group.RefreshGroupEnabled();
    }

    public async Task InitializeAsync()
    {
        var s = await _settings.LoadAsync();
        IsDryRun             = s.DryRunByDefault;
        UseRecycleBin        = s.PreferRecycleBin;
        ClearEntireDownloads = s.ClearEntireDownloads;
        LargeFileSizeMb      = s.LargeFileSizeMb;
        OldFileAgeDays       = s.OldFileAgeDays;

        // Restore persisted per-category toggles.
        SetCategory(CleanupCategory.RecycleBin,            s.CleanRecycleBin);
        SetCategory(CleanupCategory.TempFiles,             s.CleanTempFiles);
        SetCategory(CleanupCategory.DownloadedInstallers,  s.CleanDownloadedInstallers);
        SetCategory(CleanupCategory.CacheFolders,          s.CleanCacheFolders);
        SetCategory(CleanupCategory.BrowserCache,          s.CleanBrowserCache);
        SetCategory(CleanupCategory.WindowsUpdateCache,    s.CleanWindowsUpdateCache);
        SetCategory(CleanupCategory.DeliveryOptimization,  s.CleanDeliveryOptimization);
        SetCategory(CleanupCategory.WindowsErrorReporting, s.CleanWindowsErrorReports);
        SetCategory(CleanupCategory.ProgramLeftovers,      s.CleanProgramLeftovers);
        SetCategory(CleanupCategory.LargeOldFiles,         s.CleanLargeOldFiles);
        SetCategory(CleanupCategory.ThumbnailCache,        s.CleanThumbnailCache);
        SetCategory(CleanupCategory.IconCache,             s.CleanIconCache);
        SetCategory(CleanupCategory.FontCache,             s.CleanFontCache);
        SetCategory(CleanupCategory.DnsCache,              s.CleanDnsCache);
        SetCategory(CleanupCategory.PrefetchFiles,         s.CleanPrefetchFiles);
        SetCategory(CleanupCategory.StoreLogs,             s.CleanStoreLogs);

        var sessions = await _repo.GetRecentSessionsAsync(10);
        RecentSessions.Clear();
        foreach (var session in sessions.Where(s => s.Status == ScanStatus.Completed))
            RecentSessions.Add(session);

        if (RecentSessions.Count > 0 && SelectedSession is null)
            SelectedSession = RecentSessions[0];
    }

    private void SetCategory(CleanupCategory cat, bool enabled)
    {
        foreach (var group in CategoryGroups)
        {
            var opt = group.Items.FirstOrDefault(o => o.Category == cat);
            if (opt is null) continue;

            opt.IsEnabled = enabled;

            // Raise slider visibility property when LargeOldFiles is toggled.
            if (cat == CleanupCategory.LargeOldFiles)
                OnPropertyChanged(nameof(IsLargeOldFilesEnabled));

            return;
        }
    }

    // ── Analysis ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AnalyseAsync()
    {
        if (SelectedSession is null) return;

        IsLoading           = true;
        HasResults          = false;
        HasExecutionResults = false;
        UnsubscribeAllSuggestions();
        Suggestions.Clear();
        ExecutionResults.Clear();
        _lastSelectedSuggestions = [];
        StatusMessage = "Analysing…";

        try
        {
            var saved = await _settings.LoadAsync();
            var effectiveSettings = new AppSettings
            {
                PreferRecycleBin        = UseRecycleBin,
                DryRunByDefault         = saved.DryRunByDefault,
                LargeFileSizeMb         = LargeFileSizeMb,
                OldFileAgeDays          = OldFileAgeDays,
                DefaultScanPath         = saved.DefaultScanPath,
                ScanParallelism         = saved.ScanParallelism,
                ShowHiddenFiles         = saved.ShowHiddenFiles,
                SkipSystemFolders       = saved.SkipSystemFolders,
                ExcludedPaths           = saved.ExcludedPaths,
                ClearEntireDownloads    = ClearEntireDownloads,
            };

            var enabledCategories = CategoryOptions
                .Where(o => o.IsEnabled)
                .Select(o => o.Category)
                .ToHashSet();

            await foreach (var suggestion in _engine.GetSuggestionsAsync(SelectedSession.Id, effectiveSettings))
            {
                if (!enabledCategories.Contains(suggestion.Category)) continue;

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

    [RelayCommand]
    private async Task ExecuteCleanupAsync()
    {
        var selected = Suggestions
            .Where(s => s.IsSelected)
            .Select(s => s.Suggestion)
            .ToList();

        if (selected.Count == 0) return;

        _lastSelectedSuggestions = selected;

        var method = UseRecycleBin ? DeletionMethod.RecycleBin : DeletionMethod.Permanent;
        await RunCleanupCoreAsync(IsDryRun, method, selected);
    }

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
        IsExecuting           = true;
        LastRunWasDryRun      = dryRun;
        LastRunDeletionMethod = method;
        HasExecutionResults   = false;
        ExecutionResults.Clear();
        CleanupProgressValue  = 0;
        CleanupProgressText   = dryRun ? "Running dry-run preview…" : "Cleaning up…";
        StatusMessage         = CleanupProgressText;

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
            var results = await Task.Run(
                () => _engine.ExecuteAsync(suggestions, dryRun, method, progress),
                CancellationToken.None);

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

    private void UnsubscribeAllSuggestions()
    {
        foreach (var item in Suggestions)
            item.PropertyChanged -= SuggestionItem_PropertyChanged;
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
