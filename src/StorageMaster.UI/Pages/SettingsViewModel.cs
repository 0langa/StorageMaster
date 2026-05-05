using System.Collections.ObjectModel;
using System.Reflection;
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

    public ObservableCollection<string> ExcludedPaths { get; } = [];

    public string LargeFileThresholdLabel => $"Large file threshold: {LargeFileSizeMb} MB";
    public string OldFileAgeThresholdLabel => $"Old file threshold: {OldFileAgeDays} days";
    public string ScanParallelismLabel => $"Parallelism: {ScanParallelism} threads";
    public bool HasUpdateStatusMessage => !string.IsNullOrWhiteSpace(UpdateStatusMessage);
    public bool HasSavedMessage => !string.IsNullOrWhiteSpace(SavedMessage);

    public SettingsViewModel(ISettingsRepository repo, IUpdateService updateService)
    {
        _repo          = repo;
        _updateService = updateService;
    }

    partial void OnLargeFileSizeMbChanged(int value) => OnPropertyChanged(nameof(LargeFileThresholdLabel));
    partial void OnOldFileAgeDaysChanged(int value)  => OnPropertyChanged(nameof(OldFileAgeThresholdLabel));
    partial void OnScanParallelismChanged(int value) => OnPropertyChanged(nameof(ScanParallelismLabel));
    partial void OnSavedMessageChanged(string value) => OnPropertyChanged(nameof(HasSavedMessage));

    public async Task LoadAsync()
    {
        var s = await _repo.LoadAsync();
        PreferRecycleBin           = s.PreferRecycleBin;
        DryRunByDefault            = s.DryRunByDefault;
        LargeFileSizeMb            = s.LargeFileSizeMb;
        OldFileAgeDays             = s.OldFileAgeDays;
        DefaultScanPath            = s.DefaultScanPath;
        ScanParallelism            = s.ScanParallelism;
        ShowHiddenFiles            = s.ShowHiddenFiles;
        SkipSystemFolders          = s.SkipSystemFolders;
        UseTurboScanner            = s.UseTurboScanner;
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
        CheckOnStartup             = s.CheckOnStartup;
        IncludePrerelease          = s.IncludePrerelease;

        ExcludedPaths.Clear();
        foreach (var p in s.ExcludedPaths)
            ExcludedPaths.Add(p);

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
        var settings = BuildSettings();
        await _repo.SaveAsync(settings);
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

    private AppSettings BuildSettings() => new()
    {
        PreferRecycleBin           = PreferRecycleBin,
        DryRunByDefault            = DryRunByDefault,
        LargeFileSizeMb            = LargeFileSizeMb,
        OldFileAgeDays             = OldFileAgeDays,
        DefaultScanPath            = DefaultScanPath,
        ScanParallelism            = ScanParallelism,
        ShowHiddenFiles            = ShowHiddenFiles,
        SkipSystemFolders          = SkipSystemFolders,
        UseTurboScanner            = UseTurboScanner,
        CleanRecycleBin            = CleanRecycleBin,
        CleanTempFiles             = CleanTempFiles,
        CleanDownloadedInstallers  = CleanDownloadedInstallers,
        ClearEntireDownloads       = ClearEntireDownloads,
        CleanCacheFolders          = CleanCacheFolders,
        CleanBrowserCache          = CleanBrowserCache,
        CleanWindowsUpdateCache    = CleanWindowsUpdateCache,
        CleanDeliveryOptimization  = CleanDeliveryOptimization,
        CleanWindowsErrorReports   = CleanWindowsErrorReports,
        CleanProgramLeftovers      = CleanProgramLeftovers,
        CleanLargeOldFiles         = CleanLargeOldFiles,
        CheckOnStartup             = CheckOnStartup,
        IncludePrerelease          = IncludePrerelease,
        ExcludedPaths              = ExcludedPaths.ToList(),
    };
}
