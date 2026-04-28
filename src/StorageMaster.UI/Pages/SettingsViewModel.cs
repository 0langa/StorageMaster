using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Pages;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsRepository _repo;

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

    // ── UI feedback ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _savedMessage      = string.Empty;

    public ObservableCollection<string> ExcludedPaths { get; } = [];

    public string LargeFileThresholdLabel => $"Large file threshold: {LargeFileSizeMb} MB";
    public string OldFileAgeThresholdLabel => $"Old file threshold: {OldFileAgeDays} days";
    public string ScanParallelismLabel => $"Parallelism: {ScanParallelism} threads";
    public bool HasSavedMessage => !string.IsNullOrWhiteSpace(SavedMessage);

    public SettingsViewModel(ISettingsRepository repo) => _repo = repo;

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

        ExcludedPaths.Clear();
        foreach (var p in s.ExcludedPaths)
            ExcludedPaths.Add(p);
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
        ExcludedPaths              = ExcludedPaths.ToList(),
    };
}
