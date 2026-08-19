using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;

namespace StorageMaster.UI.Pages;

/// <summary>Wraps a CleanupSuggestion with UI selection state.</summary>
public sealed partial class SuggestionItem : ObservableObject
{
    public CleanupSuggestion Suggestion { get; }

    [ObservableProperty] private bool _isSelected = true;

    public string SizeDisplay => ByteSizeConverter.Format(Suggestion.EstimatedBytes);
    public string RiskDisplay => Suggestion.Risk.ToString();
    public string CategoryDisplay => Suggestion.Category.ToString();
    public string ConfidenceDisplay => Loc.Format("Cleanup_Suggestion_Confidence", $"{Suggestion.Confidence:P0}");
    public string PolicyDisplay
    {
        get
        {
            var flags = new List<string>();
            if (Suggestion.RequiresAdmin)
                flags.Add(Loc.Get("Cleanup_Policy_Admin"));
            if (!Suggestion.SupportsPermanentDelete)
                flags.Add(Loc.Get("Cleanup_Policy_NoPermanentDelete"));
            if (!Suggestion.SupportsRecycleBin)
                flags.Add(Loc.Get("Cleanup_Policy_NoRecycleBin"));
            if (!Suggestion.SupportsQuarantine)
                flags.Add(Loc.Get("Cleanup_Policy_NoQuarantine"));
            if (Suggestion.NeedsServiceStop)
                flags.Add(Loc.Get("Cleanup_Policy_ServiceStop"));

            return flags.Count == 0 ? Loc.Get("Cleanup_Policy_Standard") : string.Join(", ", flags);
        }
    }

    public SuggestionItem(CleanupSuggestion suggestion)
    {
        Suggestion = suggestion;
        // Safe/low-risk recoverable suggestions may be preselected. Medium and
        // high-risk items (for example user files or a full Downloads cleanup)
        // always require an explicit per-suggestion selection before confirmation.
        IsSelected = CleanupSuggestionSelectionPolicy.ShouldSelectByDefault(suggestion);
    }
}

/// <summary>
/// Per-category toggle item shown inside an expandable group.
/// </summary>
public sealed partial class CleanupCategoryOption : ObservableObject
{
    public CleanupCategory Category { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = string.Empty;

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

    [ObservableProperty] private bool _isExpanded = true;
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
        IsGroupEnabled = Items.Count > 0 && Items.All(i => i.IsEnabled);
        _suppressCascade = false;
    }
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupEngine _engine;
    private readonly IScanRepository _repo;
    private readonly ISettingsRepository _settings;
    private readonly DispatcherQueue _dispatcherQueue;

    // ── Analysis state ──────────────────────────────────────────────────────
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isDryRun = false;
    [ObservableProperty] private string _statusMessage = Loc.Get("Cleanup_Status_Initial");
    [ObservableProperty] private ScanSession? _selectedSession;
    [ObservableProperty] private string _totalSelectedSize = "0 B";
    [ObservableProperty] private int _selectedSuggestionCount;
    [ObservableProperty] private bool _hasResults;

    // ── Execution state ─────────────────────────────────────────────────────
    [ObservableProperty] private bool _isExecuting;
    [ObservableProperty] private string _cleanupProgressText = string.Empty;
    [ObservableProperty] private double _cleanupProgressValue;
    [ObservableProperty] private bool _hasExecutionResults;

    // ── Last-run metadata (read by code-behind to build report dialog) ───────
    [ObservableProperty] private bool _lastRunWasDryRun;
    [ObservableProperty] private DeletionMethod _lastRunDeletionMethod;
    [ObservableProperty] private string _lastRunSummary = string.Empty;
    [ObservableProperty] private bool _lastPreviewAllowsExecution;

    // ── Per-session cleanup options ──────────────────────────────────────────
    [ObservableProperty] private bool _useRecycleBin = true;
    [ObservableProperty] private bool _clearEntireDownloads = false;
    [ObservableProperty] private int _largeFileSizeMb = 500;
    [ObservableProperty] private int _oldFileAgeDays = 365;

    partial void OnLargeFileSizeMbChanged(int value)
    {
        OnPropertyChanged(nameof(LargeFileSizeLabel));
        InvalidateAnalysisForScopeChange();
    }

    partial void OnOldFileAgeDaysChanged(int value)
    {
        OnPropertyChanged(nameof(OldFileAgeLabel));
        InvalidateAnalysisForScopeChange();
    }

    partial void OnUseRecycleBinChanged(bool value) => InvalidateAnalysisForScopeChange();
    partial void OnClearEntireDownloadsChanged(bool value) => InvalidateAnalysisForScopeChange();
    public string LargeFileSizeLabel => Loc.Format("Cleanup_LargeFileThreshold_Label", $"{LargeFileSizeMb:N0}");
    public string OldFileAgeLabel => Loc.Format("Cleanup_OldFileAge_Label", $"{OldFileAgeDays:N0}");

    partial void OnSelectedSessionChanged(ScanSession? value)
    {
        OnPropertyChanged(nameof(CanAnalyse));
        InvalidateAnalysisForScopeChange();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAnalyse));
        OnPropertyChanged(nameof(CanChangeAnalysisScope));
    }

    partial void OnIsExecutingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAnalyse));
        OnPropertyChanged(nameof(CanChangeAnalysisScope));
    }
    partial void OnHasResultsChanged(bool value) => OnPropertyChanged(nameof(CanExecuteCleanup));
    partial void OnSelectedSuggestionCountChanged(int value) => OnPropertyChanged(nameof(CanExecuteCleanup));

    public bool CanAnalyse => SelectedSession is not null && !IsLoading && !IsExecuting;
    public bool CanExecuteCleanup => HasResults && !IsExecuting && SelectedSuggestionCount > 0;
    public bool CanChangeAnalysisScope => !IsLoading && !IsExecuting;

    /// <summary>
    /// Computed property for the Large &amp; Old Files slider visibility.
    /// Replaces the brittle CategoryOptions[9] index binding.
    /// </summary>
    public bool IsLargeOldFilesEnabled =>
        CategoryGroups
            .SelectMany(g => g.Items)
            .FirstOrDefault(o => o.Category == CleanupCategory.LargeOldFiles)
            ?.IsEnabled ?? false;

    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];
    public ObservableCollection<ScanSession> RecentSessions { get; } = [];
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
    private CancellationTokenSource? _analysisCts;
    private int _analysisGeneration;
    private bool _isInitializing;

    public CleanupViewModel(
        ICleanupEngine engine,
        IScanRepository repo,
        ISettingsRepository settings)
    {
        _engine = engine;
        _repo = repo;
        _settings = settings;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        BuildCategoryGroups();
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    private void BuildCategoryGroups()
    {
        CategoryGroups.Clear();

        // ── Windows System ───────────────────────────────────────────────────
        var winSystem = MakeGroup(Loc.Get("Cleanup_Group_WindowsSystem"), "");
        AddItem(winSystem, CleanupCategory.RecycleBin,
            Loc.Get("Cleanup_Category_RecycleBin_Name"), Loc.Get("Cleanup_Category_RecycleBin_Description"), enabled: true);
        AddItem(winSystem, CleanupCategory.TempFiles,
            Loc.Get("Cleanup_Category_TempFiles_Name"), Loc.Get("Cleanup_Category_TempFiles_Description"), enabled: true);
        AddItem(winSystem, CleanupCategory.WindowsUpdateCache,
            Loc.Get("Cleanup_Category_WindowsUpdateCache_Name"), Loc.Get("Cleanup_Category_WindowsUpdateCache_Description"), enabled: true);
        AddItem(winSystem, CleanupCategory.DeliveryOptimization,
            Loc.Get("Cleanup_Category_DeliveryOptimization_Name"), Loc.Get("Cleanup_Category_DeliveryOptimization_Description"), enabled: true);
        AddItem(winSystem, CleanupCategory.WindowsErrorReporting,
            Loc.Get("Cleanup_Category_WindowsErrorReporting_Name"), Loc.Get("Cleanup_Category_WindowsErrorReporting_Description"), enabled: true);
        AddItem(winSystem, CleanupCategory.ThumbnailCache,
            Loc.Get("Cleanup_Category_ThumbnailCache_Name"), Loc.Get("Cleanup_Category_ThumbnailCache_Description"), enabled: true);
        AddItem(winSystem, CleanupCategory.IconCache,
            Loc.Get("Cleanup_Category_IconCache_Name"), Loc.Get("Cleanup_Category_IconCache_Description"), enabled: true);
        AddItem(winSystem, CleanupCategory.FontCache,
            Loc.Get("Cleanup_Category_FontCache_Name"), Loc.Get("Cleanup_Category_FontCache_Description"), enabled: false);
        AddItem(winSystem, CleanupCategory.PrefetchFiles,
            Loc.Get("Cleanup_Category_PrefetchFiles_Name"), Loc.Get("Cleanup_Category_PrefetchFiles_Description"), enabled: false);
        AddItem(winSystem, CleanupCategory.DnsCache,
            Loc.Get("Cleanup_Category_DnsCache_Name"), Loc.Get("Cleanup_Category_DnsCache_Description"), enabled: true);

        // ── Browsers ─────────────────────────────────────────────────────────
        var browsers = MakeGroup(Loc.Get("Cleanup_Group_Browsers"), "");
        AddItem(browsers, CleanupCategory.BrowserCache,
            Loc.Get("Cleanup_Category_BrowserCache_Name"), Loc.Get("Cleanup_Category_BrowserCache_Description"), enabled: true);

        // ── Applications ─────────────────────────────────────────────────────
        var apps = MakeGroup(Loc.Get("Cleanup_Group_Applications"), "");
        AddItem(apps, CleanupCategory.CacheFolders,
            Loc.Get("Cleanup_Category_CacheFolders_Name"), Loc.Get("Cleanup_Category_CacheFolders_Description"), enabled: true);
        AddItem(apps, CleanupCategory.ProgramLeftovers,
            Loc.Get("Cleanup_Category_ProgramLeftovers_Name"), Loc.Get("Cleanup_Category_ProgramLeftovers_Description"), enabled: true);
        AddItem(apps, CleanupCategory.StoreLogs,
            Loc.Get("Cleanup_Category_StoreLogs_Name"), Loc.Get("Cleanup_Category_StoreLogs_Description"), enabled: true);

        // ── Downloads & Installers ────────────────────────────────────────────
        var downloads = MakeGroup(Loc.Get("Cleanup_Group_Downloads"), "");
        AddItem(downloads, CleanupCategory.DownloadedInstallers,
            Loc.Get("Cleanup_Category_DownloadedInstallers_Name"), Loc.Get("Cleanup_Category_DownloadedInstallers_Description"), enabled: true);

        // ── Files & Storage ───────────────────────────────────────────────────
        var files = MakeGroup(Loc.Get("Cleanup_Group_Files"), "");
        AddItem(files, CleanupCategory.LargeOldFiles,
            Loc.Get("Cleanup_Category_LargeOldFiles_Name"), Loc.Get("Cleanup_Category_LargeOldFiles_Description"), enabled: false);

        CategoryGroups.Add(winSystem);
        CategoryGroups.Add(browsers);
        CategoryGroups.Add(apps);
        CategoryGroups.Add(downloads);
        CategoryGroups.Add(files);
    }

    private static CleanupCategoryGroup MakeGroup(string name, string icon) =>
        new() { GroupName = name, GroupIcon = icon };

    private void AddItem(
        CleanupCategoryGroup group,
        CleanupCategory category,
        string displayName,
        string description,
        bool enabled)
    {
        var item = new CleanupCategoryOption
        {
            Category = category,
            DisplayName = displayName,
            Description = description,
            IconGlyph = string.Empty,
            IsEnabled = enabled,
        };

        // Keep group toggle in sync when individual items change.
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CleanupCategoryOption.IsEnabled)) return;
            group.RefreshGroupEnabled();
            InvalidateAnalysisForScopeChange();

            // The LargeOldFiles slider visibility is bound to IsLargeOldFilesEnabled.
            // We can't raise it from here without a back-reference to the ViewModel,
            // so the XAML binds via ViewModel.IsLargeOldFilesEnabled with Mode=OneWay
            // and we raise it in SetCategory (called from InitializeAsync).
        };

        group.Items.Add(item);
        group.RefreshGroupEnabled();
    }

    public async Task InitializeAsync(
        long? requestedSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken);
        var sessions = await _repo.GetRecentSessionsAsync(10, cancellationToken);
        var completedSessions = sessions
            .Where(session => session.Status == ScanStatus.Completed)
            .ToList();

        ScanSession? requestedSession = null;
        if (requestedSessionId is not null)
        {
            requestedSession = completedSessions
                .FirstOrDefault(session => session.Id == requestedSessionId.Value);
            if (requestedSession is null)
            {
                var persisted = await _repo
                    .GetSessionAsync(requestedSessionId.Value, cancellationToken);
                if (persisted?.Status == ScanStatus.Completed)
                {
                    requestedSession = persisted;
                    completedSessions.Insert(0, persisted);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _isInitializing = true;
        try
        {
            IsDryRun = settings.DryRunByDefault;
            UseRecycleBin = settings.PreferRecycleBin;
            ClearEntireDownloads = settings.ClearEntireDownloads;
            LargeFileSizeMb = settings.LargeFileSizeMb;
            OldFileAgeDays = settings.OldFileAgeDays;

            // Restore persisted per-category toggles.
            SetCategory(CleanupCategory.RecycleBin, settings.CleanRecycleBin);
            SetCategory(CleanupCategory.TempFiles, settings.CleanTempFiles);
            SetCategory(CleanupCategory.DownloadedInstallers, settings.CleanDownloadedInstallers);
            SetCategory(CleanupCategory.CacheFolders, settings.CleanCacheFolders);
            SetCategory(CleanupCategory.BrowserCache, settings.CleanBrowserCache);
            SetCategory(CleanupCategory.WindowsUpdateCache, settings.CleanWindowsUpdateCache);
            SetCategory(CleanupCategory.DeliveryOptimization, settings.CleanDeliveryOptimization);
            SetCategory(CleanupCategory.WindowsErrorReporting, settings.CleanWindowsErrorReports);
            SetCategory(CleanupCategory.ProgramLeftovers, settings.CleanProgramLeftovers);
            SetCategory(CleanupCategory.LargeOldFiles, settings.CleanLargeOldFiles);
            SetCategory(CleanupCategory.ThumbnailCache, settings.CleanThumbnailCache);
            SetCategory(CleanupCategory.IconCache, settings.CleanIconCache);
            SetCategory(CleanupCategory.FontCache, settings.CleanFontCache);
            SetCategory(CleanupCategory.DnsCache, settings.CleanDnsCache);
            SetCategory(CleanupCategory.PrefetchFiles, settings.CleanPrefetchFiles);
            SetCategory(CleanupCategory.StoreLogs, settings.CleanStoreLogs);

            RecentSessions.Clear();
            foreach (var session in completedSessions.DistinctBy(session => session.Id))
                RecentSessions.Add(session);

            SelectedSession = requestedSession ?? RecentSessions.FirstOrDefault();
            if (requestedSessionId is not null && requestedSession is null)
            {
                StatusMessage = Loc.Format(
                    "Cleanup_Status_SessionUnavailable",
                    requestedSessionId.Value);
            }
        }
        finally
        {
            _isInitializing = false;
        }
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
        var session = SelectedSession;
        if (session is null) return;

        var preferRecycleBin = UseRecycleBin;
        var clearEntireDownloads = ClearEntireDownloads;
        var largeFileSizeMb = LargeFileSizeMb;
        var oldFileAgeDays = OldFileAgeDays;
        var enabledCategories = CategoryOptions
            .Where(option => option.IsEnabled)
            .Select(option => option.Category)
            .ToHashSet();

        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        var analysisCts = new CancellationTokenSource();
        _analysisCts = analysisCts;
        var generation = Interlocked.Increment(ref _analysisGeneration);
        var cancellationToken = analysisCts.Token;

        IsLoading = true;
        HasResults = false;
        HasExecutionResults = false;
        UnsubscribeAllSuggestions();
        Suggestions.Clear();
        ExecutionResults.Clear();
        _lastSelectedSuggestions = [];
        StatusMessage = Loc.Get("Cleanup_Status_Analysing");

        try
        {
            var saved = await _settings.LoadAsync(cancellationToken);
            var effectiveSettings = new AppSettings
            {
                PreferRecycleBin = preferRecycleBin,
                DryRunByDefault = saved.DryRunByDefault,
                LargeFileSizeMb = largeFileSizeMb,
                OldFileAgeDays = oldFileAgeDays,
                DefaultScanPath = saved.DefaultScanPath,
                ScanParallelism = saved.ScanParallelism,
                ShowHiddenFiles = saved.ShowHiddenFiles,
                SkipSystemFolders = saved.SkipSystemFolders,
                ExcludedPaths = saved.ExcludedPaths,
                ClearEntireDownloads = clearEntireDownloads,
            };

            var discovered = new List<CleanupSuggestion>();
            await foreach (var suggestion in _engine
                               .GetSuggestionsAsync(session.Id, effectiveSettings, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (!enabledCategories.Contains(suggestion.Category)) continue;
                discovered.Add(suggestion);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _analysisGeneration) ||
                SelectedSession?.Id != session.Id)
            {
                return;
            }

            foreach (var suggestion in discovered)
            {
                var item = new SuggestionItem(suggestion);
                item.PropertyChanged += SuggestionItem_PropertyChanged;
                Suggestions.Add(item);
            }

            UpdateTotalSelected();
            HasResults = Suggestions.Count > 0;
            StatusMessage = Suggestions.Count > 0
                ? Loc.Format("Cleanup_Status_Found", Suggestions.Count)
                : Loc.Get("Cleanup_Status_NoOpportunities");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generation == Volatile.Read(ref _analysisGeneration))
                StatusMessage = Loc.Get("Cleanup_Status_AnalysisCancelled");
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _analysisGeneration))
                StatusMessage = Loc.Format("Cleanup_Status_AnalysisFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, analysisCts))
            {
                _analysisCts = null;
                analysisCts.Dispose();
                IsLoading = false;
            }
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

    public async Task<bool> RunCleanupWithMethodAsync(bool dryRun, DeletionMethod method)
    {
        if (_lastSelectedSuggestions.Count == 0 ||
            (!dryRun && (!LastRunWasDryRun || !LastPreviewAllowsExecution)))
        {
            return false;
        }

        var frozenSuggestions = _lastSelectedSuggestions;
        await RunCleanupCoreAsync(dryRun, method, frozenSuggestions);
        return true;
    }

    public string LastRunSelectedSizeDisplay => ByteSizeConverter.Format(
        _lastSelectedSuggestions.Sum(suggestion => suggestion.EstimatedBytes));

    private async Task RunCleanupCoreAsync(
        bool dryRun,
        DeletionMethod method,
        IReadOnlyList<CleanupSuggestion> suggestions)
    {
        IsExecuting = true;
        LastRunWasDryRun = dryRun;
        LastRunDeletionMethod = method;
        LastPreviewAllowsExecution = false;
        HasExecutionResults = false;
        ExecutionResults.Clear();
        CleanupProgressValue = 0;
        CleanupProgressText = dryRun
            ? Loc.Get("Cleanup_Progress_DryRun")
            : Loc.Get("Cleanup_Progress_Cleaning");
        StatusMessage = CleanupProgressText;

        var dq = _dispatcherQueue;
        var progress = new Progress<CleanupProgress>(p =>
        {
            void Apply()
            {
                CleanupProgressValue = p.Total > 0
                    ? (double)p.Completed / p.Total * 100.0
                    : 0;
                CleanupProgressText = p.Completed < p.Total
                    ? Loc.Format("Cleanup_Progress_Item", p.Completed + 1, p.Total, p.CurrentTitle)
                    : dryRun
                        ? Loc.Get("Cleanup_Progress_PreviewComplete")
                        : Loc.Get("Cleanup_Progress_Complete");
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
                var error = BuildResultError(r);
                ExecutionResults.Add(new CleanupResultDisplay(
                    suggestions.First(s => s.Id == r.SuggestionId).Title,
                    r.Status.ToString(),
                    ByteSizeConverter.Format(r.BytesFreed),
                    r.WasDryRun,
                    error));
            }
            HasExecutionResults = ExecutionResults.Count > 0;

            long totalFreed = results.Sum(r => r.BytesFreed);
            int succeeded = results.Count(r => r.Status == CleanupResultStatus.Success);
            int partial = results.Count(r => r.Status == CleanupResultStatus.PartialSuccess);
            int failed = results.Count(r => r.Status == CleanupResultStatus.Failed);
            int skipped = results.Count(r => r.Status == CleanupResultStatus.Skipped);
            LastPreviewAllowsExecution = dryRun &&
                CleanupPreviewExecutionPolicy.CanExecuteAfterPreview(
                    results,
                    suggestions.Select(suggestion => suggestion.Id).ToArray());
            if (dryRun && !LastPreviewAllowsExecution)
                _lastSelectedSuggestions = [];

            LastRunSummary = dryRun
                ? BuildPreviewSummary(totalFreed, succeeded, partial, failed, skipped)
                : BuildSummaryText(totalFreed, succeeded, partial, failed, skipped, method);

            StatusMessage = LastRunSummary;
        }
        catch (Exception ex)
        {
            LastPreviewAllowsExecution = false;
            if (dryRun)
                _lastSelectedSuggestions = [];
            LastRunSummary = Loc.Format("Cleanup_Status_CleanupFailed", ex.Message);
            StatusMessage = LastRunSummary;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private static string BuildPreviewSummary(
        long estimatedBytes,
        int succeeded,
        int partial,
        int failed,
        int skipped)
    {
        var summary = Loc.Format(
            "Safety_Cleanup_Summary_Preview",
            ByteSizeConverter.Format(estimatedBytes),
            succeeded,
            partial,
            failed,
            skipped);
        var resolveFailures = Loc.Get("Safety_Cleanup_Summary_ResolveFailures");
        return partial == 0 && failed == 0 && skipped == 0
            ? summary
            : $"{summary} {resolveFailures}";
    }

    private static string BuildSummaryText(
        long freed,
        int succeeded,
        int partial,
        int failed,
        int skipped,
        DeletionMethod method)
    {
        var sb = new System.Text.StringBuilder();
        if (method == DeletionMethod.RecycleBin)
        {
            sb.Append(Loc.Format(
                "Safety_Cleanup_Summary_RecycleBin",
                ByteSizeConverter.Format(freed)));
        }
        else
        {
            sb.Append(Loc.Format(
                "Safety_Cleanup_Summary_Permanent",
                ByteSizeConverter.Format(freed)));
        }
        sb.Append(' ');
        sb.Append(Loc.Format(
            "Safety_Cleanup_Summary_Counts",
            succeeded,
            partial,
            failed,
            skipped));
        sb.Append('.');
        return sb.ToString();
    }

    private static string? BuildResultError(CleanupResult result)
    {
        var failedPathSummary = result.FailedPaths.Count switch
        {
            0 => null,
            1 => Loc.Format("Safety_Cleanup_Report_OneTargetFailed", result.FailedPaths[0]),
            _ => Loc.Format(
                "Safety_Cleanup_Report_TargetsFailed",
                result.FailedPaths.Count,
                result.FailedPaths[0]),
        };

        if (string.IsNullOrWhiteSpace(result.ErrorMessage))
            return failedPathSummary;
        if (failedPathSummary is null)
            return result.ErrorMessage;
        return $"{failedPathSummary}. {result.ErrorMessage}";
    }

    private void InvalidateAnalysisForScopeChange()
    {
        if (_isInitializing)
            return;

        Interlocked.Increment(ref _analysisGeneration);
        _analysisCts?.Cancel();
        _lastSelectedSuggestions = [];
        LastPreviewAllowsExecution = false;

        if (Suggestions.Count > 0 || ExecutionResults.Count > 0)
        {
            UnsubscribeAllSuggestions();
            Suggestions.Clear();
            ExecutionResults.Clear();
            HasResults = false;
            HasExecutionResults = false;
            UpdateTotalSelected();
        }

        StatusMessage = Loc.Get("Cleanup_Status_ScopeChanged");
    }

    public void CancelPendingAnalysis()
    {
        Interlocked.Increment(ref _analysisGeneration);
        _analysisCts?.Cancel();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void UpdateTotalSelected()
    {
        long total = Suggestions
            .Where(s => s.IsSelected)
            .Sum(s => s.Suggestion.EstimatedBytes);
        TotalSelectedSize = ByteSizeConverter.Format(total);
        SelectedSuggestionCount = Suggestions.Count(s => s.IsSelected);
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
    string Title,
    string Status,
    string BytesFreed,
    bool WasDryRun,
    string? Error);
