using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Pages;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _repo;
    private readonly IUpdateService      _updateService;
    private readonly IScanRepository     _scanRepository;
    private AppSettings _loadedSettings = new();

    private CancellationTokenSource? _downloadCts;

    // ── Deletion ────────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _preferRecycleBin  = true;
    [ObservableProperty] private bool   _dryRunByDefault   = false;

    // ── Thresholds ──────────────────────────────────────────────────────────
    [ObservableProperty] private int    _largeFileSizeMb   = 500;
    [ObservableProperty] private int    _oldFileAgeDays    = 365;

    // ── Scan ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _defaultScanPath   = @"C:\";
    [ObservableProperty] private int    _scanParallelism   = 4;
    [ObservableProperty] private bool   _showHiddenFiles   = false;
    [ObservableProperty] private bool   _skipSystemFolders = true;
    [ObservableProperty] private bool   _useTurboScanner   = false;
    [ObservableProperty] private ThemePreference _theme = ThemePreference.Default;
    [ObservableProperty] private int    _scanHistoryRetentionDays = 365;

    // ── Cleanup default rule toggles ─────────────────────────────────────
    [ObservableProperty] private bool   _cleanRecycleBin           = true;
    [ObservableProperty] private bool   _cleanTempFiles            = true;
    [ObservableProperty] private bool   _cleanDownloadedInstallers = true;
    [ObservableProperty] private bool   _clearEntireDownloads      = false;
    [ObservableProperty] private bool   _cleanCacheFolders         = true;
    [ObservableProperty] private bool   _cleanBrowserCache         = true;
    [ObservableProperty] private bool   _cleanWindowsUpdateCache   = true;
    [ObservableProperty] private bool   _cleanDeliveryOptimization = true;
    [ObservableProperty] private bool   _cleanWindowsErrorReports  = true;
    [ObservableProperty] private bool   _cleanProgramLeftovers     = true;
    [ObservableProperty] private bool   _cleanLargeOldFiles        = false;
    [ObservableProperty] private bool   _cleanThumbnailCache       = true;
    [ObservableProperty] private bool   _cleanIconCache            = true;
    [ObservableProperty] private bool   _cleanFontCache            = false;
    [ObservableProperty] private bool   _cleanDnsCache             = true;
    [ObservableProperty] private bool   _cleanPrefetchFiles        = false;
    [ObservableProperty] private bool   _cleanStoreLogs            = true;

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

    // ── Update preferences ───────────────────────────────────────────────────
    [ObservableProperty] private bool   _checkOnStartup    = true;
    [ObservableProperty] private bool   _includePrerelease = false;

    // ── Update state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool         _isCheckingForUpdates;
    [ObservableProperty] private bool         _isDownloadingUpdate;
    [ObservableProperty] private double       _downloadProgress;
    [ObservableProperty] private string       _updateStatusMessage = string.Empty;
    [ObservableProperty] private UpdateInfo?  _availableUpdate;

    partial void OnIsCheckingForUpdatesChanged(bool value)   => OnPropertyChanged(nameof(CanCheckForUpdates));
    partial void OnIsDownloadingUpdateChanged(bool value)    => OnPropertyChanged(nameof(CanDownloadAndInstall));
    partial void OnUpdateStatusMessageChanged(string value)  => OnPropertyChanged(nameof(HasUpdateStatusMessage));
    partial void OnAvailableUpdateChanged(UpdateInfo? value)
    {
        OnPropertyChanged(nameof(HasUpdateAvailable));
        OnPropertyChanged(nameof(UpdateAvailableText));
        OnPropertyChanged(nameof(ReleaseNotesUri));
        OnPropertyChanged(nameof(CanDownloadAndInstall));
    }

    public bool   CanCheckForUpdates    => !IsCheckingForUpdates && !IsDownloadingUpdate;
    public bool   HasUpdateAvailable    => AvailableUpdate is not null;
    public bool   CanDownloadAndInstall => HasUpdateAvailable && !IsDownloadingUpdate && !IsCheckingForUpdates;

    public string CurrentVersion => Assembly.GetEntryAssembly()
        ?.GetName().Version?.ToString(3) ?? "Unknown";

    public string UpdateAvailableText => AvailableUpdate is { } u
        ? $"Version {u.Version.ToString(3)} is available  (released {u.PublishedAt:d MMMM yyyy})"
        : string.Empty;

    public Uri? ReleaseNotesUri => AvailableUpdate is { } u
        ? new Uri($"https://github.com/0langa/StorageMaster/releases/tag/{u.TagName}")
        : null;

    // ── UI feedback ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _savedMessage = string.Empty;
    [ObservableProperty] private bool   _isPurgingHistory;
    [ObservableProperty] private string _defaultScanPathError = string.Empty;
    [ObservableProperty] private string _ffmpegPathError = string.Empty;

    public ObservableCollection<string> ExcludedPaths { get; } = [];
    public Array ThemeOptions => Enum.GetValues(typeof(ThemePreference));
    public Array KeeperPolicyOptions => Enum.GetValues(typeof(KeeperPolicy));

    public string LargeFileThresholdLabel => $"Large file threshold: {LargeFileSizeMb} MB";
    public string OldFileAgeThresholdLabel => $"Old file threshold: {OldFileAgeDays} days";
    public string ScanParallelismLabel => $"Parallelism: {ScanParallelism} threads";
    public string ScanHistoryRetentionLabel => $"Keep scan history for {ScanHistoryRetentionDays} days";
    public bool HasUpdateStatusMessage => !string.IsNullOrWhiteSpace(UpdateStatusMessage);
    public bool HasSavedMessage => !string.IsNullOrWhiteSpace(SavedMessage);
    public bool CanPurgeHistory => !IsPurgingHistory;
    public bool HasDefaultScanPathError => !string.IsNullOrWhiteSpace(DefaultScanPathError);
    public bool HasFfmpegPathError => !string.IsNullOrWhiteSpace(FfmpegPathError);
    public bool CanSave => !HasDefaultScanPathError && !HasFfmpegPathError;

    public SettingsViewModel(
        ISettingsRepository repo,
        IUpdateService updateService,
        IScanRepository scanRepository)
    {
        _repo          = repo;
        _updateService = updateService;
        _scanRepository = scanRepository;
    }

    partial void OnLargeFileSizeMbChanged(int value) => OnPropertyChanged(nameof(LargeFileThresholdLabel));
    partial void OnOldFileAgeDaysChanged(int value)  => OnPropertyChanged(nameof(OldFileAgeThresholdLabel));
    partial void OnScanParallelismChanged(int value) => OnPropertyChanged(nameof(ScanParallelismLabel));
    partial void OnScanHistoryRetentionDaysChanged(int value) => OnPropertyChanged(nameof(ScanHistoryRetentionLabel));
    partial void OnSavedMessageChanged(string value) => OnPropertyChanged(nameof(HasSavedMessage));
    partial void OnIsPurgingHistoryChanged(bool value) => OnPropertyChanged(nameof(CanPurgeHistory));
    partial void OnDefaultScanPathChanged(string value) => ValidateDefaultScanPath();
    partial void OnFfmpegPathChanged(string value) => ValidateFfmpegPath();
    partial void OnDefaultScanPathErrorChanged(string value) => OnPropertyChanged(nameof(HasDefaultScanPathError));
    partial void OnFfmpegPathErrorChanged(string value) => OnPropertyChanged(nameof(HasFfmpegPathError));

    public async Task LoadAsync()
    {
        var s = await _repo.LoadAsync();
        _loadedSettings = CloneSettings(s);
        PreferRecycleBin           = s.PreferRecycleBin;
        DryRunByDefault            = s.DryRunByDefault;
        LargeFileSizeMb            = s.LargeFileSizeMb;
        OldFileAgeDays             = s.OldFileAgeDays;
        DefaultScanPath            = s.DefaultScanPath;
        ScanParallelism            = s.ScanParallelism;
        ShowHiddenFiles            = s.ShowHiddenFiles;
        SkipSystemFolders          = s.SkipSystemFolders;
        UseTurboScanner            = s.UseTurboScanner;
        Theme                      = s.Theme;
        ScanHistoryRetentionDays   = s.ScanHistoryRetentionDays;
        CleanRecycleBin            = s.CleanRecycleBin;
        CleanTempFiles             = s.CleanTempFiles;
        CleanDownloadedInstallers  = s.CleanDownloadedInstallers;
        ClearEntireDownloads       = s.ClearEntireDownloads;
        CleanCacheFolders          = s.CleanCacheFolders;
        CleanBrowserCache          = s.CleanBrowserCache;
        CleanWindowsUpdateCache    = s.CleanWindowsUpdateCache;
        CleanDeliveryOptimization  = s.CleanDeliveryOptimization;
        CleanWindowsErrorReports   = s.CleanWindowsErrorReports;
        CleanProgramLeftovers      = s.CleanProgramLeftovers;
        CleanLargeOldFiles         = s.CleanLargeOldFiles;
        CleanThumbnailCache        = s.CleanThumbnailCache;
        CleanIconCache             = s.CleanIconCache;
        CleanFontCache             = s.CleanFontCache;
        CleanDnsCache              = s.CleanDnsCache;
        CleanPrefetchFiles         = s.CleanPrefetchFiles;
        CleanStoreLogs             = s.CleanStoreLogs;
        DuplicateMinimumSizeMb     = s.DuplicateMinimumSizeMb;
        DuplicateKeeperPolicy      = s.DuplicateKeeperPolicy;
        DuplicateUseNormalizedText = s.DuplicateUseNormalizedText;
        DuplicateUseImagePHash     = s.DuplicateUseImagePHash;
        DuplicateUseVideoPHash     = s.DuplicateUseVideoPHash;
        DuplicateImagePHashThreshold = s.DuplicateImagePHashThreshold;
        DuplicateVideoFrameThreshold = s.DuplicateVideoFrameThreshold;
        DuplicateMaxVideoDurationSeconds = s.DuplicateMaxVideoDurationSeconds;
        FfmpegPath                 = s.FfmpegPath;
        CheckOnStartup             = s.CheckOnStartup;
        IncludePrerelease          = s.IncludePrerelease;

        ExcludedPaths.Clear();
        foreach (var p in s.ExcludedPaths)
            ExcludedPaths.Add(p);

        ValidateDefaultScanPath();
        ValidateFfmpegPath();
        OnPropertyChanged(nameof(CanSave));

        // Reflect any update already found by the startup background check.
        AvailableUpdate = _updateService.LastCheckResult;
        if (AvailableUpdate is not null)
            UpdateStatusMessage = $"Update to {AvailableUpdate.Version.ToString(3)} is ready to download.";
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
    private async Task SaveAsync()
    {
        ValidateDefaultScanPath();
        ValidateFfmpegPath();
        OnPropertyChanged(nameof(CanSave));
        if (!CanSave)
        {
            SavedMessage = "Fix validation errors before saving.";
            await Task.Delay(3000);
            SavedMessage = string.Empty;
            return;
        }

        var settings = BuildSettings();
        await _repo.SaveAsync(settings);
        _loadedSettings = CloneSettings(settings);
        SavedMessage = "Settings saved.";
        await Task.Delay(3000);
        SavedMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        await _repo.SaveAsync(new AppSettings());
        await LoadAsync();
        SavedMessage = "Settings reset to defaults.";
        await Task.Delay(3000);
        SavedMessage = string.Empty;
    }

    // ── Update commands ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusMessage  = "Checking for updates…";
        AvailableUpdate      = null;

        try
        {
            var info = await _updateService.CheckAsync(IncludePrerelease);
            AvailableUpdate = info;
            UpdateStatusMessage = info is not null
                ? $"Update to {info.Version.ToString(3)} is available."
                : "No compatible update is currently available.";
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallAsync()
    {
        if (AvailableUpdate is null) return;

        _downloadCts     = new CancellationTokenSource();
        IsDownloadingUpdate = true;
        DownloadProgress    = 0;
        UpdateStatusMessage = "Downloading update…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress    = p;
                UpdateStatusMessage = $"Downloading… {p:F0}%";
            });

            var path = await _updateService.DownloadAsync(
                AvailableUpdate, progress, _downloadCts.Token);

            UpdateStatusMessage = "Launching installer…";

            if (_updateService.LaunchInstaller(path))
                Application.Current.Exit();
            else
                UpdateStatusMessage = "Could not launch installer. Run it manually from %TEMP%\\StorageMaster\\Updates\\.";
        }
        catch (OperationCanceledException)
        {
            UpdateStatusMessage = "Download cancelled.";
            DownloadProgress    = 0;
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"Download failed: {ex.Message}";
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

    [RelayCommand]
    private async Task PurgeOldHistoryAsync()
    {
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
            SavedMessage = deletions.Length > 0
                ? $"Deleted {deletions.Length} old scan session(s)."
                : "No scan history matched the current retention window.";
            await Task.Delay(3000);
            SavedMessage = string.Empty;
        }
        finally
        {
            IsPurgingHistory = false;
        }
    }

    private AppSettings BuildSettings()
    {
        var settings = CloneSettings(_loadedSettings);
        settings.PreferRecycleBin           = PreferRecycleBin;
        settings.DryRunByDefault            = DryRunByDefault;
        settings.LargeFileSizeMb            = LargeFileSizeMb;
        settings.OldFileAgeDays             = OldFileAgeDays;
        settings.DefaultScanPath            = DefaultScanPath;
        settings.ScanParallelism            = ScanParallelism;
        settings.ShowHiddenFiles            = ShowHiddenFiles;
        settings.SkipSystemFolders          = SkipSystemFolders;
        settings.UseTurboScanner            = UseTurboScanner;
        settings.Theme                      = Theme;
        settings.ScanHistoryRetentionDays   = ScanHistoryRetentionDays;
        settings.CleanRecycleBin            = CleanRecycleBin;
        settings.CleanTempFiles             = CleanTempFiles;
        settings.CleanDownloadedInstallers  = CleanDownloadedInstallers;
        settings.ClearEntireDownloads       = ClearEntireDownloads;
        settings.CleanCacheFolders          = CleanCacheFolders;
        settings.CleanBrowserCache          = CleanBrowserCache;
        settings.CleanWindowsUpdateCache    = CleanWindowsUpdateCache;
        settings.CleanDeliveryOptimization  = CleanDeliveryOptimization;
        settings.CleanWindowsErrorReports   = CleanWindowsErrorReports;
        settings.CleanProgramLeftovers      = CleanProgramLeftovers;
        settings.CleanLargeOldFiles         = CleanLargeOldFiles;
        settings.CleanThumbnailCache        = CleanThumbnailCache;
        settings.CleanIconCache             = CleanIconCache;
        settings.CleanFontCache             = CleanFontCache;
        settings.CleanDnsCache              = CleanDnsCache;
        settings.CleanPrefetchFiles         = CleanPrefetchFiles;
        settings.CleanStoreLogs             = CleanStoreLogs;
        settings.DuplicateMinimumSizeMb     = DuplicateMinimumSizeMb;
        settings.DuplicateKeeperPolicy      = DuplicateKeeperPolicy;
        settings.DuplicateUseNormalizedText = DuplicateUseNormalizedText;
        settings.DuplicateUseImagePHash     = DuplicateUseImagePHash;
        settings.DuplicateUseVideoPHash     = DuplicateUseVideoPHash;
        settings.DuplicateImagePHashThreshold = DuplicateImagePHashThreshold;
        settings.DuplicateVideoFrameThreshold = DuplicateVideoFrameThreshold;
        settings.DuplicateMaxVideoDurationSeconds = DuplicateMaxVideoDurationSeconds;
        settings.FfmpegPath                 = FfmpegPath;
        settings.CheckOnStartup             = CheckOnStartup;
        settings.IncludePrerelease          = IncludePrerelease;
        settings.ExcludedPaths              = ExcludedPaths.ToList();
        return settings;
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
        FfmpegPathError = string.IsNullOrWhiteSpace(FfmpegPath)
            ? string.Empty
            : !File.Exists(FfmpegPath)
                ? "FFmpeg path does not exist."
                : !string.Equals(Path.GetExtension(FfmpegPath), ".exe", StringComparison.OrdinalIgnoreCase)
                    ? "FFmpeg path must point to ffmpeg.exe."
                    : string.Empty;
        OnPropertyChanged(nameof(CanSave));
    }

    private static AppSettings CloneSettings(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)) ?? new AppSettings();
}
