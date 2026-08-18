using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scheduling;
using StorageMaster.Core.Update;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class ScheduledJobEditorItem : ObservableObject
{
    public ScheduledTaskInfo Info { get; }
    public string Summary =>
        $"{Info.Job.Kind} · {Info.Job.Frequency} · {Info.Job.StartTimeLocal}" +
        (string.IsNullOrWhiteSpace(Info.Job.TargetPath) ? string.Empty : $" · {Info.Job.TargetPath}");

    public ScheduledJobEditorItem(ScheduledTaskInfo info) => Info = info;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _repo;
    private readonly IUpdateService _updateService;
    private readonly IScanRepository _scanRepository;
    private readonly ILocalDiagnosticsService _diagnostics;
    private readonly IScheduledTaskService _scheduledTaskService;
    private readonly StartupRegistrationService _startupRegistration;
    private AppSettings _loadedSettings = new();
    private AppSettings? _editorSnapshot;

    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _messageCts;

    // ── Deletion ────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _preferRecycleBin = true;
    [ObservableProperty] private bool _dryRunByDefault = false;

    // ── Thresholds ──────────────────────────────────────────────────────────
    [ObservableProperty] private int _largeFileSizeMb = 500;
    [ObservableProperty] private int _oldFileAgeDays = 365;

    // ── Scan ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _defaultScanPath = @"C:\";
    [ObservableProperty] private int _scanParallelism = 4;
    [ObservableProperty] private bool _showHiddenFiles = false;
    [ObservableProperty] private bool _skipSystemFolders = true;
    [ObservableProperty] private bool _useTurboScanner = false;
    [ObservableProperty] private ThemePreference _theme = ThemePreference.Default;
    [ObservableProperty] private int _scanHistoryRetentionDays = 365;

    // ── Cleanup default rule toggles ─────────────────────────────────────
    [ObservableProperty] private bool _cleanRecycleBin = true;
    [ObservableProperty] private bool _cleanTempFiles = true;
    [ObservableProperty] private bool _cleanDownloadedInstallers = true;
    [ObservableProperty] private bool _clearEntireDownloads = false;
    [ObservableProperty] private bool _cleanCacheFolders = true;
    [ObservableProperty] private bool _cleanBrowserCache = true;
    [ObservableProperty] private bool _cleanWindowsUpdateCache = true;
    [ObservableProperty] private bool _cleanDeliveryOptimization = true;
    [ObservableProperty] private bool _cleanWindowsErrorReports = true;
    [ObservableProperty] private bool _cleanProgramLeftovers = true;
    [ObservableProperty] private bool _cleanLargeOldFiles = false;
    [ObservableProperty] private bool _cleanThumbnailCache = true;
    [ObservableProperty] private bool _cleanIconCache = true;
    [ObservableProperty] private bool _cleanFontCache = false;
    [ObservableProperty] private bool _cleanDnsCache = true;
    [ObservableProperty] private bool _cleanPrefetchFiles = false;
    [ObservableProperty] private bool _cleanStoreLogs = true;

    // ── Dedup defaults ───────────────────────────────────────────────────────
    [ObservableProperty] private int _duplicateMinimumSizeMb = 1;
    [ObservableProperty] private KeeperPolicy _duplicateKeeperPolicy = KeeperPolicy.Newest;
    [ObservableProperty] private bool _duplicateUseNormalizedText;
    [ObservableProperty] private bool _duplicateUseImagePHash;
    [ObservableProperty] private bool _duplicateUseVideoPHash;
    [ObservableProperty] private int _duplicateImagePHashThreshold = 6;
    [ObservableProperty] private int _duplicateVideoFrameThreshold = 8;
    [ObservableProperty] private int _duplicateMaxVideoDurationSeconds = 1800;
    [ObservableProperty] private string _ffmpegPath = string.Empty;

    // ── UI preferences ───────────────────────────────────────────────────────
    [ObservableProperty] private UiDensity _uiDensity = UiDensity.Default;
    [ObservableProperty] private bool _reduceAnimations;
    [ObservableProperty] private string _defaultLandingPage = "Dashboard";
    [ObservableProperty] private int _defaultResultsPageSize = 100;
    [ObservableProperty] private string _defaultDuplicatesReviewMode = "Exact";
    [ObservableProperty] private bool _expandAdvancedOptionsByDefault;

    // ── Update preferences ───────────────────────────────────────────────────
    [ObservableProperty] private bool _checkOnStartup = true;
    [ObservableProperty] private bool _includePrerelease = false;
    [ObservableProperty] private bool _requireSignedUpdates;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _startTrayOnLogin;
    [ObservableProperty] private bool _enableLowDiskNotifications = true;
    [ObservableProperty] private bool _enableDriveHealthNotifications = true;
    [ObservableProperty] private int _lowDiskWarningPercent = 15;
    [ObservableProperty] private int _lowDiskCriticalPercent = 5;
    [ObservableProperty] private bool _scheduledTasksEnabled;

    // ── Scheduler editor ────────────────────────────────────────────────────
    [ObservableProperty] private ScheduledJobEditorItem? _selectedScheduledJob;
    [ObservableProperty] private string _scheduledJobName = string.Empty;
    [ObservableProperty] private ScheduledJobKind _scheduledJobKind = ScheduledJobKind.Scan;
    [ObservableProperty] private ScheduledJobFrequency _scheduledJobFrequency = ScheduledJobFrequency.Daily;
    [ObservableProperty] private DayOfWeek _scheduledJobDay = DayOfWeek.Monday;
    [ObservableProperty] private string _scheduledJobTime = "09:00";
    [ObservableProperty] private string _scheduledJobTargetPath = @"C:\";
    [ObservableProperty] private string _scheduledJobRulesCsv = string.Empty;
    [ObservableProperty] private bool _scheduledJobEnabled = true;
    [ObservableProperty] private bool _isSavingScheduledJob;

    // ── Update state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _updateStatusMessage = string.Empty;
    [ObservableProperty] private UpdateInfo? _availableUpdate;

    // ── Hub / editor state ───────────────────────────────────────────────────
    [ObservableProperty] private SettingsCategory _selectedCategory = SettingsCategory.General;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _editorValidationMessage = string.Empty;

    partial void OnIsCheckingForUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanDownloadAndInstall));
        NotifyUpdateCommandStates();
    }

    partial void OnIsDownloadingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(CanDownloadAndInstall));
        NotifyUpdateCommandStates();
    }

    partial void OnUpdateStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasUpdateStatusMessage));
    partial void OnAvailableUpdateChanged(UpdateInfo? value)
    {
        OnPropertyChanged(nameof(HasUpdateAvailable));
        OnPropertyChanged(nameof(UpdateAvailableText));
        OnPropertyChanged(nameof(ReleaseNotesUri));
        OnPropertyChanged(nameof(CanDownloadAndInstall));
        NotifyUpdateCommandStates();
    }

    partial void OnSelectedCategoryChanged(SettingsCategory value)
    {
        OnPropertyChanged(nameof(SelectedCategoryTitle));
        OnPropertyChanged(nameof(IsGeneralSelected));
        OnPropertyChanged(nameof(IsScanningSelected));
        OnPropertyChanged(nameof(IsCleanupSelected));
        OnPropertyChanged(nameof(IsDuplicatesSelected));
        OnPropertyChanged(nameof(IsResultsHistorySelected));
        OnPropertyChanged(nameof(IsSchedulingSelected));
        OnPropertyChanged(nameof(IsTrayNotificationsSelected));
        OnPropertyChanged(nameof(IsUpdatesSelected));
        OnPropertyChanged(nameof(IsAdvancedDiagnosticsSelected));
    }

    partial void OnSearchQueryChanged(string value) => FilterCategories();

    public bool CanCheckForUpdates => !IsCheckingForUpdates && !IsDownloadingUpdate;
    public bool HasUpdateAvailable => AvailableUpdate is not null;
    public bool CanDownloadAndInstall => HasUpdateAvailable && !IsDownloadingUpdate && !IsCheckingForUpdates;

    public string CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? assembly.GetName().Version?.ToString(3)
                ?? "Unknown";
        }
    }

    public string UpdateAvailableText => AvailableUpdate is { } u
        ? $"Version {u.TagName.TrimStart('v', 'V')} is available  (released {u.PublishedAt:d MMMM yyyy})"
        : string.Empty;

    public Uri? ReleaseNotesUri => AvailableUpdate is { } u
        ? new Uri(u.ReleaseUrl)
        : null;

    // ── UI feedback ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _savedMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _feedbackSeverity = InfoBarSeverity.Success;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _loadError = string.Empty;
    [ObservableProperty] private bool _isPurgingHistory;
    [ObservableProperty] private string _defaultScanPathError = string.Empty;
    [ObservableProperty] private string _ffmpegPathError = string.Empty;
    [ObservableProperty] private bool _isExportingDiagnostics;

    public ObservableCollection<string> ExcludedPaths { get; } = [];
    public ObservableCollection<ScheduledJobEditorItem> ScheduledJobs { get; } = [];
    public Array ThemeOptions => Enum.GetValues(typeof(ThemePreference));
    public Array UiDensityOptions => Enum.GetValues(typeof(UiDensity));
    public Array KeeperPolicyOptions => Enum.GetValues(typeof(KeeperPolicy));
    public Array ScheduledJobKindOptions => Enum.GetValues(typeof(ScheduledJobKind));
    public Array ScheduledJobFrequencyOptions => Enum.GetValues(typeof(ScheduledJobFrequency));
    public Array ScheduledJobDayOptions => Enum.GetValues(typeof(DayOfWeek));

    public string LargeFileThresholdLabel => $"Large file threshold: {LargeFileSizeMb} MB";
    public string OldFileAgeThresholdLabel => $"Old file threshold: {OldFileAgeDays} days";
    public string ScanParallelismLabel => $"Parallelism: {ScanParallelism} threads";
    public string ScanHistoryRetentionLabel => $"Keep scan history for {ScanHistoryRetentionDays} days";
    public bool HasUpdateStatusMessage => !string.IsNullOrWhiteSpace(UpdateStatusMessage);
    public bool HasSavedMessage => !string.IsNullOrWhiteSpace(SavedMessage);
    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);
    public bool CanPurgeHistory => IsLoaded && !IsPurgingHistory;
    public bool HasDefaultScanPathError => !string.IsNullOrWhiteSpace(DefaultScanPathError);
    public bool HasFfmpegPathError => !string.IsNullOrWhiteSpace(FfmpegPathError);
    public bool CanSave => IsLoaded && !HasDefaultScanPathError && !HasFfmpegPathError;
    public bool CanExportDiagnostics => IsLoaded && !IsExportingDiagnostics;
    public bool HasScheduledJobSelection => SelectedScheduledJob is not null;
    public bool CanSaveScheduledJob => IsLoaded && !IsSavingScheduledJob;
    public bool CanDeleteScheduledJob => IsLoaded && SelectedScheduledJob is not null && !IsSavingScheduledJob;
    public bool RequiresDestructiveScheduledJobConsent =>
        ScheduledJobKind == StorageMaster.Core.Models.ScheduledJobKind.CleanupExecuteSafe &&
        ScheduledJobEnabled;
    public string FfmpegDetectionText => BuildFfmpegDetectionText();

    public string SelectedCategoryTitle => SelectedCategory switch
    {
        SettingsCategory.General => "General & Appearance",
        SettingsCategory.Scanning => "Scanning & Performance",
        SettingsCategory.Cleanup => "Cleanup & Safety",
        SettingsCategory.Duplicates => "Duplicates & Matching",
        SettingsCategory.ResultsHistory => "Results & History",
        SettingsCategory.Scheduling => "Scheduling & Automation",
        SettingsCategory.TrayNotifications => "Background, Tray & Notifications",
        SettingsCategory.Updates => "Updates & Security",
        SettingsCategory.AdvancedDiagnostics => "Advanced Diagnostics & About",
        _ => "Settings",
    };

    public bool IsGeneralSelected => SelectedCategory == SettingsCategory.General;
    public bool IsScanningSelected => SelectedCategory == SettingsCategory.Scanning;
    public bool IsCleanupSelected => SelectedCategory == SettingsCategory.Cleanup;
    public bool IsDuplicatesSelected => SelectedCategory == SettingsCategory.Duplicates;
    public bool IsResultsHistorySelected => SelectedCategory == SettingsCategory.ResultsHistory;
    public bool IsSchedulingSelected => SelectedCategory == SettingsCategory.Scheduling;
    public bool IsTrayNotificationsSelected => SelectedCategory == SettingsCategory.TrayNotifications;
    public bool IsUpdatesSelected => SelectedCategory == SettingsCategory.Updates;
    public bool IsAdvancedDiagnosticsSelected => SelectedCategory == SettingsCategory.AdvancedDiagnostics;

    public ObservableCollection<SettingsCategoryItem> FilteredCategories { get; } = [];

    private readonly SettingsCategoryItem[] _allCategories;

    public SettingsViewModel(
        ISettingsRepository repo,
        IUpdateService updateService,
        IScanRepository scanRepository,
        ILocalDiagnosticsService diagnostics,
        IScheduledTaskService scheduledTaskService,
        StartupRegistrationService startupRegistration)
    {
        _repo = repo;
        _updateService = updateService;
        _scanRepository = scanRepository;
        _diagnostics = diagnostics;
        _scheduledTaskService = scheduledTaskService;
        _startupRegistration = startupRegistration;

        _allCategories =
        [
            new SettingsCategoryItem(SettingsCategory.General, "General & Appearance", "Theme, density, landing page, and scan path.", "\uE771"),
            new SettingsCategoryItem(SettingsCategory.Scanning, "Scanning & Performance", "Parallelism, hidden files, exclusions, and retention.", "\uE8B5"),
            new SettingsCategoryItem(SettingsCategory.Cleanup, "Cleanup & Safety", "Deletion behaviour, thresholds, and default rules.", "\uE74C"),
            new SettingsCategoryItem(SettingsCategory.Duplicates, "Duplicates & Matching", "Detection methods, keeper policy, and FFmpeg.", "\uE8B7"),
            new SettingsCategoryItem(SettingsCategory.ResultsHistory, "Results & History", "History retention and default page size.", "\uE81C"),
            new SettingsCategoryItem(SettingsCategory.Scheduling, "Scheduling & Automation", "Scheduled scans, cleanup jobs, and triggers.", "\uE8C0"),
            new SettingsCategoryItem(SettingsCategory.TrayNotifications, "Background, Tray & Notifications", "Tray minimise, startup, and low-disk alerts.", "\uE7E8"),
            new SettingsCategoryItem(SettingsCategory.Updates, "Updates & Security", "Startup checks, pre-releases, and signing.", "\uE8C3"),
            new SettingsCategoryItem(SettingsCategory.AdvancedDiagnostics, "Advanced Diagnostics & About", "Diagnostics export and version info.", "\uE9CE"),
        ];

        foreach (var c in _allCategories)
            FilteredCategories.Add(c);
    }

    partial void OnLargeFileSizeMbChanged(int value) => OnPropertyChanged(nameof(LargeFileThresholdLabel));
    partial void OnOldFileAgeDaysChanged(int value) => OnPropertyChanged(nameof(OldFileAgeThresholdLabel));
    partial void OnScanParallelismChanged(int value) => OnPropertyChanged(nameof(ScanParallelismLabel));
    partial void OnScanHistoryRetentionDaysChanged(int value) => OnPropertyChanged(nameof(ScanHistoryRetentionLabel));
    partial void OnSavedMessageChanged(string value) => OnPropertyChanged(nameof(HasSavedMessage));
    partial void OnLoadErrorChanged(string value) => OnPropertyChanged(nameof(HasLoadError));
    partial void OnIsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanPurgeHistory));
        OnPropertyChanged(nameof(CanExportDiagnostics));
        OnPropertyChanged(nameof(CanSaveScheduledJob));
        OnPropertyChanged(nameof(CanDeleteScheduledJob));
    }
    partial void OnIsPurgingHistoryChanged(bool value) => OnPropertyChanged(nameof(CanPurgeHistory));
    partial void OnIsExportingDiagnosticsChanged(bool value) => OnPropertyChanged(nameof(CanExportDiagnostics));
    partial void OnIsSavingScheduledJobChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSaveScheduledJob));
        OnPropertyChanged(nameof(CanDeleteScheduledJob));
    }
    partial void OnDefaultScanPathChanged(string value) => ValidateDefaultScanPath();
    partial void OnScheduledJobKindChanged(ScheduledJobKind value)
    {
        if (value == StorageMaster.Core.Models.ScheduledJobKind.CleanupExecuteSafe &&
            SelectedScheduledJob is null)
            ScheduledJobEnabled = false;
        OnPropertyChanged(nameof(RequiresDestructiveScheduledJobConsent));
    }

    partial void OnScheduledJobEnabledChanged(bool value) =>
        OnPropertyChanged(nameof(RequiresDestructiveScheduledJobConsent));
    partial void OnFfmpegPathChanged(string value)
    {
        ValidateFfmpegPath();
        OnPropertyChanged(nameof(FfmpegDetectionText));
    }
    partial void OnSelectedScheduledJobChanged(ScheduledJobEditorItem? value)
    {
        OnPropertyChanged(nameof(HasScheduledJobSelection));
        OnPropertyChanged(nameof(CanDeleteScheduledJob));
        if (value is null)
            return;

        ScheduledJobName = value.Info.Job.Name;
        ScheduledJobKind = value.Info.Job.Kind;
        ScheduledJobFrequency = value.Info.Job.Frequency;
        ScheduledJobDay = value.Info.Job.WeeklyDay;
        ScheduledJobTime = value.Info.Job.StartTimeLocal;
        ScheduledJobTargetPath = value.Info.Job.TargetPath;
        ScheduledJobRulesCsv = value.Info.Job.RulesCsv;
        ScheduledJobEnabled = value.Info.Job.Enabled;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoaded = false;
        LoadError = string.Empty;
        SavedMessage = string.Empty;

        var s = await _repo.LoadAsync(cancellationToken);
        _loadedSettings = CloneSettings(s);
        ApplySettings(s);

        ExcludedPaths.Clear();
        foreach (var p in s.ExcludedPaths)
            ExcludedPaths.Add(p);

        await RefreshScheduledJobsCoreAsync(cancellationToken);
        SelectedScheduledJob = ScheduledJobs.FirstOrDefault();

        ValidateDefaultScanPath();
        ValidateFfmpegPath();
        OnPropertyChanged(nameof(FfmpegDetectionText));
        OnPropertyChanged(nameof(CanSave));

        AvailableUpdate = _updateService.LastCheckResult;
        if (AvailableUpdate is not null)
            UpdateStatusMessage = $"Update to {AvailableUpdate.Version.ToString(3)} is ready to download.";

        RefreshCategorySummaries();
        IsLoaded = true;
    }

    public void ReportLoadFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        IsLoaded = false;
        LoadError = exception.Message;
        FeedbackSeverity = InfoBarSeverity.Error;
        SavedMessage =
            $"Settings could not be loaded. Editing is disabled to avoid overwriting stored values: {exception.Message}";
    }

    public void AddExcludedPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !ExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            ExcludedPaths.Add(path);
    }

    public void RemoveExcludedPathEntry(string path) => ExcludedPaths.Remove(path);

    [RelayCommand]
    private void RemoveExcludedPath(string path) => ExcludedPaths.Remove(path);

    [RelayCommand]
    private void OpenCategory(SettingsCategory category)
    {
        if (!IsLoaded)
            return;

        SelectedCategory = category;
        _editorSnapshot = CloneSettings(BuildSettings());
        EditorValidationMessage = string.Empty;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
        _editorSnapshot = null;
    }

    [RelayCommand]
    private void CancelCategory()
    {
        if (_editorSnapshot is not null)
            ApplySettings(_editorSnapshot);

        IsEditorOpen = false;
        _editorSnapshot = null;
    }

    [RelayCommand]
    private void ResetCategory()
    {
        var defaults = new AppSettings();
        switch (SelectedCategory)
        {
            case SettingsCategory.General:
                Theme = defaults.Theme;
                DefaultScanPath = defaults.DefaultScanPath;
                UiDensity = defaults.UiDensity;
                ReduceAnimations = defaults.ReduceAnimations;
                DefaultLandingPage = defaults.DefaultLandingPage;
                ExpandAdvancedOptionsByDefault = defaults.ExpandAdvancedOptionsByDefault;
                break;
            case SettingsCategory.Scanning:
                ScanParallelism = defaults.ScanParallelism;
                UseTurboScanner = defaults.UseTurboScanner;
                ShowHiddenFiles = defaults.ShowHiddenFiles;
                SkipSystemFolders = defaults.SkipSystemFolders;
                ScanHistoryRetentionDays = defaults.ScanHistoryRetentionDays;
                ExcludedPaths.Clear();
                break;
            case SettingsCategory.Cleanup:
                PreferRecycleBin = defaults.PreferRecycleBin;
                DryRunByDefault = defaults.DryRunByDefault;
                LargeFileSizeMb = defaults.LargeFileSizeMb;
                OldFileAgeDays = defaults.OldFileAgeDays;
                CleanRecycleBin = defaults.CleanRecycleBin;
                CleanTempFiles = defaults.CleanTempFiles;
                CleanDownloadedInstallers = defaults.CleanDownloadedInstallers;
                ClearEntireDownloads = defaults.ClearEntireDownloads;
                CleanCacheFolders = defaults.CleanCacheFolders;
                CleanBrowserCache = defaults.CleanBrowserCache;
                CleanWindowsUpdateCache = defaults.CleanWindowsUpdateCache;
                CleanDeliveryOptimization = defaults.CleanDeliveryOptimization;
                CleanWindowsErrorReports = defaults.CleanWindowsErrorReports;
                CleanProgramLeftovers = defaults.CleanProgramLeftovers;
                CleanLargeOldFiles = defaults.CleanLargeOldFiles;
                CleanThumbnailCache = defaults.CleanThumbnailCache;
                CleanIconCache = defaults.CleanIconCache;
                CleanFontCache = defaults.CleanFontCache;
                CleanDnsCache = defaults.CleanDnsCache;
                CleanPrefetchFiles = defaults.CleanPrefetchFiles;
                CleanStoreLogs = defaults.CleanStoreLogs;
                break;
            case SettingsCategory.Duplicates:
                DuplicateMinimumSizeMb = defaults.DuplicateMinimumSizeMb;
                DuplicateKeeperPolicy = defaults.DuplicateKeeperPolicy;
                DuplicateUseNormalizedText = defaults.DuplicateUseNormalizedText;
                DuplicateUseImagePHash = defaults.DuplicateUseImagePHash;
                DuplicateUseVideoPHash = defaults.DuplicateUseVideoPHash;
                DuplicateImagePHashThreshold = defaults.DuplicateImagePHashThreshold;
                DuplicateVideoFrameThreshold = defaults.DuplicateVideoFrameThreshold;
                DuplicateMaxVideoDurationSeconds = defaults.DuplicateMaxVideoDurationSeconds;
                FfmpegPath = defaults.FfmpegPath;
                DefaultDuplicatesReviewMode = defaults.DefaultDuplicatesReviewMode;
                break;
            case SettingsCategory.ResultsHistory:
                ScanHistoryRetentionDays = defaults.ScanHistoryRetentionDays;
                DefaultResultsPageSize = defaults.DefaultResultsPageSize;
                break;
            case SettingsCategory.Scheduling:
                ScheduledTasksEnabled = defaults.ScheduledTasksEnabled;
                break;
            case SettingsCategory.TrayNotifications:
                MinimizeToTray = defaults.MinimizeToTray;
                StartTrayOnLogin = defaults.StartTrayOnLogin;
                EnableLowDiskNotifications = defaults.EnableLowDiskNotifications;
                EnableDriveHealthNotifications = defaults.EnableDriveHealthNotifications;
                LowDiskWarningPercent = defaults.LowDiskWarningPercent;
                LowDiskCriticalPercent = defaults.LowDiskCriticalPercent;
                break;
            case SettingsCategory.Updates:
                CheckOnStartup = defaults.CheckOnStartup;
                IncludePrerelease = defaults.IncludePrerelease;
                RequireSignedUpdates = defaults.RequireSignedUpdates;
                break;
            case SettingsCategory.AdvancedDiagnostics:
                break;
        }

        ValidateDefaultScanPath();
        ValidateFfmpegPath();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        EditorValidationMessage = string.Empty;
        ValidateDefaultScanPath();
        ValidateFfmpegPath();
        OnPropertyChanged(nameof(CanSave));
        if (!CanSave)
        {
            EditorValidationMessage = "Fix validation errors before saving.";
            return;
        }

        var persisted = false;
        try
        {
            // Atomic update: applies only the editor-owned properties onto the
            // freshest stored settings, so concurrent writers (low-disk monitor,
            // scheduler run-outcomes) are not clobbered by a stale whole snapshot.
            var settings = await _repo.UpdateAsync(ApplyEditsTo);
            persisted = true;
            _startupRegistration.SetEnabled(StartTrayOnLogin);
            _loadedSettings = CloneSettings(settings);
            _editorSnapshot = CloneSettings(settings);
            RefreshCategorySummaries();
            await ShowSavedMessageAsync("Settings saved.");
        }
        catch (Exception ex)
        {
            ReportOperationFailure(
                persisted
                    ? "Settings were stored, but Windows startup registration failed. Review the startup setting before retrying."
                    : "Settings were not saved.",
                ex);
        }
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        if (!IsLoaded)
            return;

        try
        {
            await _repo.SaveAsync(new AppSettings());
            await LoadAsync();
            await ShowSavedMessageAsync("Settings reset to defaults.");
        }
        catch (Exception ex)
        {
            ReportOperationFailure(
                "Settings reset did not complete. Reload Settings before editing again.",
                ex);
            IsLoaded = false;
        }
    }

    // ── Update commands ───────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusMessage = "Checking for updates…";
        AvailableUpdate = null;

        try
        {
            var info = await _updateService.CheckAsync(IncludePrerelease);
            AvailableUpdate = info;
            UpdateStatusMessage = info is not null
                ? $"Update to {info.Version.ToString(3)} is available."
                : "No compatible update is currently available.";
            await RecordDiagnosticsBestEffortAsync(UpdateStatusMessage);
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"Update check failed: {ex.Message}";
            await RecordDiagnosticsBestEffortAsync(UpdateStatusMessage);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadAndInstall))]
    private async Task DownloadAndInstallAsync()
    {
        if (AvailableUpdate is null) return;

        _downloadCts = new CancellationTokenSource();
        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        UpdateStatusMessage = "Downloading update…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                UpdateStatusMessage = $"Downloading… {p:F0}%";
            });

            var path = await _updateService.DownloadAsync(
                AvailableUpdate, progress, _downloadCts.Token);

            UpdateStatusMessage = "Launching installer…";

            if (await _updateService.LaunchInstallerAsync(path, _downloadCts.Token))
                Application.Current.Exit();
            else
                UpdateStatusMessage = _updateService.LastFailureKind == UpdateFailureKind.UserCancelledElevation
                    ? "Installer launch cancelled at the elevation prompt."
                    : "Could not launch installer. Run it manually from %TEMP%\\StorageMaster\\Updates\\.";
            await RecordDiagnosticsBestEffortAsync(UpdateStatusMessage);
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage = "Download cancelled.";
            DownloadProgress = 0;
        }
        catch (UpdateException ex)
        {
            UpdateStatusMessage = ex.Kind switch
            {
                UpdateFailureKind.DownloadFileInUse =>
                    "Download failed: installer file is locked by another process. Close open installers and retry.",
                UpdateFailureKind.ChecksumMismatch =>
                    "Download failed: checksum verification mismatch. Please retry.",
                UpdateFailureKind.InvalidSignature =>
                    "Download failed: installer trust verification failed.",
                UpdateFailureKind.NetworkTimeout =>
                    "Download failed: network timeout while fetching release asset.",
                UpdateFailureKind.MissingInstallerAsset =>
                    "Download failed: release installer asset is missing.",
                UpdateFailureKind.InsecureDownloadUrl =>
                    "Download failed: updater refused a non-HTTPS download URL.",
                _ => $"Download failed: {ex.Message}",
            };
            await RecordDiagnosticsBestEffortAsync(UpdateStatusMessage);
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"Download failed: {ex.Message}";
            await RecordDiagnosticsBestEffortAsync(UpdateStatusMessage);
        }
        finally
        {
            IsDownloadingUpdate = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    private void NotifyUpdateCommandStates()
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        DownloadAndInstallCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task PurgeOldHistoryAsync()
    {
        if (!IsLoaded)
            return;

        IsPurgingHistory = true;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, ScanHistoryRetentionDays));
            var sessions = await _scanRepository.GetRecentSessionsAsync(500);
            var deletions = sessions
                .Where(static session => session.CompletedUtc is not null)
                .Where(session => session.CompletedUtc!.Value < cutoff)
                .Select(session => _scanRepository.DeleteSessionAsync(session.Id))
                .ToArray();

            await Task.WhenAll(deletions);
            await ShowSavedMessageAsync(deletions.Length > 0
                ? $"Deleted {deletions.Length} old scan session(s)."
                : "No scan history matched the current retention window.");
        }
        catch (Exception ex)
        {
            ReportOperationFailure(
                "History purge stopped. Some sessions may already have been deleted; refresh scan history before retrying.",
                ex);
        }
        finally
        {
            IsPurgingHistory = false;
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (!IsLoaded)
            return;

        IsExportingDiagnostics = true;
        try
        {
            var bundlePath = await _diagnostics.ExportBundleAsync();
            await ShowSavedMessageAsync($"Diagnostics bundle exported: {bundlePath}", 4000);
        }
        catch (Exception ex)
        {
            ReportOperationFailure("Diagnostics export failed; no complete bundle was confirmed.", ex);
        }
        finally
        {
            IsExportingDiagnostics = false;
        }
    }

    private AppSettings BuildSettings()
    {
        var settings = CloneSettings(_loadedSettings);
        ApplyEditsTo(settings);
        return settings;
    }

    /// <summary>Applies every editor-owned property onto <paramref name="settings"/>.</summary>
    private void ApplyEditsTo(AppSettings settings)
    {
        settings.PreferRecycleBin = PreferRecycleBin;
        settings.DryRunByDefault = DryRunByDefault;
        settings.LargeFileSizeMb = LargeFileSizeMb;
        settings.OldFileAgeDays = OldFileAgeDays;
        settings.DefaultScanPath = DefaultScanPath;
        settings.ScanParallelism = ScanParallelism;
        settings.ShowHiddenFiles = ShowHiddenFiles;
        settings.SkipSystemFolders = SkipSystemFolders;
        settings.UseTurboScanner = UseTurboScanner;
        settings.Theme = Theme;
        settings.ScanHistoryRetentionDays = ScanHistoryRetentionDays;
        settings.CleanRecycleBin = CleanRecycleBin;
        settings.CleanTempFiles = CleanTempFiles;
        settings.CleanDownloadedInstallers = CleanDownloadedInstallers;
        settings.ClearEntireDownloads = ClearEntireDownloads;
        settings.CleanCacheFolders = CleanCacheFolders;
        settings.CleanBrowserCache = CleanBrowserCache;
        settings.CleanWindowsUpdateCache = CleanWindowsUpdateCache;
        settings.CleanDeliveryOptimization = CleanDeliveryOptimization;
        settings.CleanWindowsErrorReports = CleanWindowsErrorReports;
        settings.CleanProgramLeftovers = CleanProgramLeftovers;
        settings.CleanLargeOldFiles = CleanLargeOldFiles;
        settings.CleanThumbnailCache = CleanThumbnailCache;
        settings.CleanIconCache = CleanIconCache;
        settings.CleanFontCache = CleanFontCache;
        settings.CleanDnsCache = CleanDnsCache;
        settings.CleanPrefetchFiles = CleanPrefetchFiles;
        settings.CleanStoreLogs = CleanStoreLogs;
        settings.DuplicateMinimumSizeMb = DuplicateMinimumSizeMb;
        settings.DuplicateKeeperPolicy = DuplicateKeeperPolicy;
        settings.DuplicateUseNormalizedText = DuplicateUseNormalizedText;
        settings.DuplicateUseImagePHash = DuplicateUseImagePHash;
        settings.DuplicateUseVideoPHash = DuplicateUseVideoPHash;
        settings.DuplicateImagePHashThreshold = DuplicateImagePHashThreshold;
        settings.DuplicateVideoFrameThreshold = DuplicateVideoFrameThreshold;
        settings.DuplicateMaxVideoDurationSeconds = DuplicateMaxVideoDurationSeconds;
        settings.FfmpegPath = FfmpegPathNormalizer.Normalize(FfmpegPath);
        settings.CheckOnStartup = CheckOnStartup;
        settings.IncludePrerelease = IncludePrerelease;
        settings.RequireSignedUpdates = RequireSignedUpdates;
        settings.MinimizeToTray = MinimizeToTray;
        settings.StartTrayOnLogin = StartTrayOnLogin;
        settings.EnableLowDiskNotifications = EnableLowDiskNotifications;
        settings.EnableDriveHealthNotifications = EnableDriveHealthNotifications;
        settings.LowDiskWarningPercent = Math.Clamp(LowDiskWarningPercent, 1, 99);
        settings.LowDiskCriticalPercent = Math.Clamp(LowDiskCriticalPercent, 1, 99);
        settings.ScheduledTasksEnabled = ScheduledTasksEnabled;
        settings.ExcludedPaths = ExcludedPaths.ToList();
        settings.UiDensity = UiDensity;
        settings.ReduceAnimations = ReduceAnimations;
        settings.DefaultLandingPage = DefaultLandingPage;
        settings.DefaultResultsPageSize = Math.Clamp(DefaultResultsPageSize, 10, 1000);
        settings.DefaultDuplicatesReviewMode = DefaultDuplicatesReviewMode;
        settings.ExpandAdvancedOptionsByDefault = ExpandAdvancedOptionsByDefault;
    }

    private void ApplySettings(AppSettings s)
    {
        PreferRecycleBin = s.PreferRecycleBin;
        DryRunByDefault = s.DryRunByDefault;
        LargeFileSizeMb = s.LargeFileSizeMb;
        OldFileAgeDays = s.OldFileAgeDays;
        DefaultScanPath = s.DefaultScanPath;
        ScanParallelism = s.ScanParallelism;
        ShowHiddenFiles = s.ShowHiddenFiles;
        SkipSystemFolders = s.SkipSystemFolders;
        UseTurboScanner = s.UseTurboScanner;
        Theme = s.Theme;
        ScanHistoryRetentionDays = s.ScanHistoryRetentionDays;
        CleanRecycleBin = s.CleanRecycleBin;
        CleanTempFiles = s.CleanTempFiles;
        CleanDownloadedInstallers = s.CleanDownloadedInstallers;
        ClearEntireDownloads = s.ClearEntireDownloads;
        CleanCacheFolders = s.CleanCacheFolders;
        CleanBrowserCache = s.CleanBrowserCache;
        CleanWindowsUpdateCache = s.CleanWindowsUpdateCache;
        CleanDeliveryOptimization = s.CleanDeliveryOptimization;
        CleanWindowsErrorReports = s.CleanWindowsErrorReports;
        CleanProgramLeftovers = s.CleanProgramLeftovers;
        CleanLargeOldFiles = s.CleanLargeOldFiles;
        CleanThumbnailCache = s.CleanThumbnailCache;
        CleanIconCache = s.CleanIconCache;
        CleanFontCache = s.CleanFontCache;
        CleanDnsCache = s.CleanDnsCache;
        CleanPrefetchFiles = s.CleanPrefetchFiles;
        CleanStoreLogs = s.CleanStoreLogs;
        DuplicateMinimumSizeMb = s.DuplicateMinimumSizeMb;
        DuplicateKeeperPolicy = s.DuplicateKeeperPolicy;
        DuplicateUseNormalizedText = s.DuplicateUseNormalizedText;
        DuplicateUseImagePHash = s.DuplicateUseImagePHash;
        DuplicateUseVideoPHash = s.DuplicateUseVideoPHash;
        DuplicateImagePHashThreshold = s.DuplicateImagePHashThreshold;
        DuplicateVideoFrameThreshold = s.DuplicateVideoFrameThreshold;
        DuplicateMaxVideoDurationSeconds = s.DuplicateMaxVideoDurationSeconds;
        FfmpegPath = FfmpegPathNormalizer.Normalize(s.FfmpegPath);
        CheckOnStartup = s.CheckOnStartup;
        IncludePrerelease = s.IncludePrerelease;
        RequireSignedUpdates = s.RequireSignedUpdates;
        MinimizeToTray = s.MinimizeToTray;
        StartTrayOnLogin = s.StartTrayOnLogin || _startupRegistration.IsEnabled();
        EnableLowDiskNotifications = s.EnableLowDiskNotifications;
        EnableDriveHealthNotifications = s.EnableDriveHealthNotifications;
        LowDiskWarningPercent = s.LowDiskWarningPercent;
        LowDiskCriticalPercent = s.LowDiskCriticalPercent;
        ScheduledTasksEnabled = s.ScheduledTasksEnabled;
        UiDensity = s.UiDensity;
        ReduceAnimations = s.ReduceAnimations;
        DefaultLandingPage = s.DefaultLandingPage;
        DefaultResultsPageSize = s.DefaultResultsPageSize;
        DefaultDuplicatesReviewMode = s.DefaultDuplicatesReviewMode;
        ExpandAdvancedOptionsByDefault = s.ExpandAdvancedOptionsByDefault;
    }

    private void ValidateDefaultScanPath()
    {
        DefaultScanPathError = string.IsNullOrWhiteSpace(DefaultScanPath)
            ? "Default scan path is required."
            : !Path.IsPathRooted(DefaultScanPath)
                ? "Default scan path must be an absolute path."
                : !Directory.Exists(DefaultScanPath)
                    ? "Default scan path does not exist."
                    : string.Empty;
        OnPropertyChanged(nameof(CanSave));
    }

    private void ValidateFfmpegPath()
    {
        var normalized = FfmpegPathNormalizer.Normalize(FfmpegPath);
        if (!string.Equals(FfmpegPath, normalized, StringComparison.Ordinal))
            FfmpegPath = normalized;

        var resolved = FfmpegToolResolver.Resolve(normalized, AppContext.BaseDirectory);

        FfmpegPathError = string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : !resolved.HasFfmpeg
                ? "FFmpeg path does not exist."
                : !string.Equals(Path.GetFileName(normalized), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                    ? "FFmpeg path must point to ffmpeg.exe."
                    : !resolved.HasFfprobe
                        ? "ffprobe.exe must be in the same folder as ffmpeg.exe."
                    : string.Empty;
        OnPropertyChanged(nameof(CanSave));
    }

    private string BuildFfmpegDetectionText()
    {
        var resolved = FfmpegToolResolver.Resolve(FfmpegPath, AppContext.BaseDirectory);
        if (resolved.IsComplete)
        {
            var prefix = string.IsNullOrWhiteSpace(FfmpegPath)
                ? $"Auto-detected from {resolved.Source}"
                : "Ready";
            return $"{prefix}: {resolved.FfmpegPath}";
        }

        if (resolved.HasFfmpeg)
            return "Found ffmpeg.exe, but ffprobe.exe is missing beside it.";

        return "Tip: leave this blank if StorageMaster bundles FFmpeg in tools\\ffmpeg or if FFmpeg is on PATH.";
    }

    private static AppSettings CloneSettings(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();

    [RelayCommand]
    private async Task RefreshScheduledJobsAsync()
    {
        if (!IsLoaded)
            return;

        try
        {
            await RefreshScheduledJobsCoreAsync();
            await ShowSavedMessageAsync("Scheduled jobs refreshed.", 2000);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(
                "Scheduled jobs could not be refreshed. Existing editor state was preserved.",
                ex);
        }
    }

    private async Task RefreshScheduledJobsCoreAsync(CancellationToken cancellationToken = default)
    {
        var jobs = (await _scheduledTaskService.ListAsync(cancellationToken))
            .Select(job => new ScheduledJobEditorItem(job))
            .ToList();

        ScheduledJobs.Clear();
        foreach (var job in jobs)
            ScheduledJobs.Add(job);
        OnPropertyChanged(nameof(HasScheduledJobSelection));
    }

    [RelayCommand]
    private void NewScheduledJob()
    {
        SelectedScheduledJob = null;
        ScheduledJobName = string.Empty;
        ScheduledJobKind = ScheduledJobKind.Scan;
        ScheduledJobFrequency = ScheduledJobFrequency.Daily;
        ScheduledJobDay = DayOfWeek.Monday;
        ScheduledJobTime = "09:00";
        ScheduledJobTargetPath = DefaultScanPath;
        ScheduledJobRulesCsv = string.Empty;
        ScheduledJobEnabled = true;
    }

    public bool TryCreateScheduledJobDraft(out ScheduledJobDefinition job)
    {
        try
        {
            var draft = new ScheduledJobDefinition
            {
                Id = SelectedScheduledJob?.Info.Job.Id ?? Guid.NewGuid().ToString("N"),
                Name = ScheduledJobName,
                Kind = ScheduledJobKind,
                Frequency = ScheduledJobFrequency,
                WeeklyDay = ScheduledJobDay,
                StartTimeLocal = ScheduledJobTime,
                TargetPath = ScheduledJobTargetPath,
                RulesCsv = ScheduledJobRulesCsv,
                Enabled = ScheduledJobEnabled,
                DestructiveConsentVersion = 0,
                DestructiveConsentFingerprint = string.Empty,
            };

            job = draft.Kind == StorageMaster.Core.Models.ScheduledJobKind.CleanupExecuteSafe
                ? ScheduledCleanupPolicy.NormalizeConsentFields(draft)
                : draft;
            return true;
        }
        catch (Exception ex)
        {
            job = null!;
            ReportOperationFailure("Scheduled job was not saved because its plan is invalid.", ex);
            return false;
        }
    }

    public void ReportScheduledJobUiFailure(Exception exception) =>
        ReportOperationFailure("Scheduled job operation failed.", exception);

    public async Task SaveScheduledJobAsync(ScheduledJobDefinition job)
    {
        if (!IsLoaded)
            return;

        if (job.Kind == StorageMaster.Core.Models.ScheduledJobKind.CleanupExecuteSafe &&
            job.Enabled &&
            !ScheduledJobExecutionPolicy.Evaluate(scheduledTasksEnabled: true, job).CanExecute)
        {
            FeedbackSeverity = InfoBarSeverity.Warning;
            SavedMessage =
                "Enabled scheduled cleanup requires current consent for its exact target, rules, schedule, and Recycle Bin safety policy.";
            return;
        }

        EditorValidationMessage = string.Empty;
        IsSavingScheduledJob = true;
        var taskMutationCompleted = false;
        try
        {
            await _scheduledTaskService.UpsertAsync(job);
            taskMutationCompleted = true;
            await RefreshScheduledJobsCoreAsync();
            ScheduledTasksEnabled = ScheduledJobs.Any(item => item.Info.Job.Enabled);
            SelectedScheduledJob = ScheduledJobs.FirstOrDefault(item => item.Info.Job.Id == job.Id);
            await ShowSavedMessageAsync("Scheduled job saved.", 2500);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(
                taskMutationCompleted
                    ? "Scheduled job was saved, but the editor refresh failed. Do not retry until you refresh the job list."
                    : "Scheduled job was not saved; prior task/settings state was restored where possible.",
                ex);
        }
        finally
        {
            IsSavingScheduledJob = false;
        }
    }

    [RelayCommand]
    private async Task DeleteScheduledJobAsync()
    {
        if (SelectedScheduledJob is null)
            return;

        EditorValidationMessage = string.Empty;
        IsSavingScheduledJob = true;
        var deletionCompleted = false;
        try
        {
            await _scheduledTaskService.DeleteAsync(SelectedScheduledJob.Info.Job.Id);
            deletionCompleted = true;
            await RefreshScheduledJobsCoreAsync();
            NewScheduledJob();
            await ShowSavedMessageAsync("Scheduled job deleted.", 2500);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(
                deletionCompleted
                    ? "Scheduled job was deleted, but the editor refresh failed. Refresh before another change."
                    : "Scheduled job deletion failed; prior task/settings state was restored where possible.",
                ex);
        }
        finally
        {
            IsSavingScheduledJob = false;
        }
    }

    private async Task ShowSavedMessageAsync(string message, int durationMs = 3000)
    {
        _messageCts?.Cancel();
        _messageCts?.Dispose();
        _messageCts = new CancellationTokenSource();
        var token = _messageCts.Token;

        FeedbackSeverity = InfoBarSeverity.Success;
        SavedMessage = message;
        try
        {
            await Task.Delay(durationMs, token);
            if (!token.IsCancellationRequested)
                SavedMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ReportOperationFailure(string context, Exception exception)
    {
        FeedbackSeverity = InfoBarSeverity.Error;
        SavedMessage = $"{context} {exception.Message}";
        EditorValidationMessage = SavedMessage;
    }

    private async Task RecordDiagnosticsBestEffortAsync(string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _diagnostics.RecordAsync("updater", message, timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch
        {
            // User-visible updater result must not be replaced by diagnostics failure.
        }
    }

    private void FilterCategories()
    {
        FilteredCategories.Clear();
        var query = SearchQuery.Trim();
        foreach (var c in _allCategories)
        {
            if (string.IsNullOrWhiteSpace(query)
                || c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredCategories.Add(c);
            }
        }
    }

    private void RefreshCategorySummaries()
    {
        foreach (var c in _allCategories)
        {
            switch (c.Category)
            {
                case SettingsCategory.General:
                    c.StatusSummary = $"{Theme}";
                    c.HasWarning = false;
                    break;
                case SettingsCategory.Scanning:
                    c.StatusSummary = $"{ScanParallelism} threads · {ExcludedPaths.Count} exclusions";
                    c.HasWarning = false;
                    break;
                case SettingsCategory.Cleanup:
                    c.StatusSummary = PreferRecycleBin ? "Recycle Bin preferred" : "Permanent delete enabled";
                    c.HasWarning = !PreferRecycleBin;
                    c.WarningText = "Permanent deletion is risky. Enable Recycle Bin for safety.";
                    break;
                case SettingsCategory.Duplicates:
                    c.StatusSummary = $"{DuplicateKeeperPolicy} keeper · {DuplicateMinimumSizeMb} MB min";
                    c.HasWarning = false;
                    break;
                case SettingsCategory.ResultsHistory:
                    c.StatusSummary = $"{ScanHistoryRetentionDays} days retention";
                    c.HasWarning = false;
                    break;
                case SettingsCategory.Scheduling:
                    c.StatusSummary = $"{ScheduledJobs.Count} job(s)";
                    c.HasWarning = false;
                    break;
                case SettingsCategory.TrayNotifications:
                    c.StatusSummary = MinimizeToTray ? "Tray enabled" : "Tray disabled";
                    c.HasWarning = false;
                    break;
                case SettingsCategory.Updates:
                    c.StatusSummary = CheckOnStartup ? "Auto-check on" : "Manual checks";
                    c.HasWarning = !RequireSignedUpdates;
                    c.WarningText = "Signed updates are recommended for security.";
                    break;
                case SettingsCategory.AdvancedDiagnostics:
                    c.StatusSummary = $"v{CurrentVersion}";
                    c.HasWarning = false;
                    break;
            }
        }
    }
}
