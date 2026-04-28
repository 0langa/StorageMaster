using CommunityToolkit.Mvvm.ComponentModel;
using StorageMaster.Platform.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class ScanViewModel : ObservableObject
{
    private readonly IFileScanner       _scanner;
    private readonly IFileScanner       _turboScanner;
    private readonly IDriveInfoProvider _drives;
    private readonly INavigationService _nav;
    private readonly IAdminService      _admin;
    private readonly ISettingsRepository _settings;

    private CancellationTokenSource? _cts;

    [ObservableProperty] private string  _selectedPath         = @"C:\";
    [ObservableProperty] private bool    _isScanning;
    [ObservableProperty] private bool    _scanComplete;
    [ObservableProperty] private string  _progressText         = string.Empty;
    [ObservableProperty] private string  _currentFile          = string.Empty;
    [ObservableProperty] private long    _filesScanned;
    [ObservableProperty] private long    _foldersScanned;
    [ObservableProperty] private string  _bytesScanned         = "0 B";
    [ObservableProperty] private int     _errorCount;
    [ObservableProperty] private double  _progressValue;
    [ObservableProperty] private string  _errorMessage         = string.Empty;
    [ObservableProperty] private bool    _hasError;
    [ObservableProperty] private IReadOnlyList<DriveDetail> _availableDrives = [];
    [ObservableProperty] private bool    _deepScan;
    [ObservableProperty] private bool    _useTurboScanner;
    [ObservableProperty] private bool    _turboScannerAvailable;

    /// <summary>True when the process already holds administrator privileges.</summary>
    public bool IsRunningAsAdmin => _admin.IsRunningAsAdmin;

    /// <summary>
    /// True when deep scan is on but we are NOT running as admin —
    /// the user should be prompted to elevate.
    /// </summary>
    public bool NeedsElevation => DeepScan && !IsRunningAsAdmin;

    partial void OnDeepScanChanged(bool value) => OnPropertyChanged(nameof(NeedsElevation));

    private long _lastSessionId;

    public ScanViewModel(
        IFileScanner        scanner,
        IFileScanner        turboScanner,
        IDriveInfoProvider  drives,
        INavigationService  nav,
        IAdminService       admin,
        ISettingsRepository settings)
    {
        _scanner      = scanner;
        _turboScanner = turboScanner;
        _drives       = drives;
        _nav          = nav;
        _admin        = admin;
        _settings     = settings;
    }

    public async Task InitializeAsync(bool autoEnableDeepScan = false)
    {
        var settings = await _settings.LoadAsync();
        AvailableDrives        = _drives.GetAvailableDrives();
        ScanComplete           = false;
        HasError               = false;
        ErrorMessage           = string.Empty;
        SelectedPath           = string.IsNullOrWhiteSpace(settings.DefaultScanPath) ? @"C:\" : settings.DefaultScanPath;
        UseTurboScanner        = settings.UseTurboScanner;
        TurboScannerAvailable  = StorageMaster.Platform.Windows.TurboFileScanner.IsAvailable;
        if (autoEnableDeepScan)
            DeepScan = true;
    }

    [RelayCommand]
    private void RequestElevation() => _admin.RestartAsAdmin(enableDeepScan: true);

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning) return;

        IsScanning   = true;
        ScanComplete = false;
        HasError     = false;
        ErrorMessage = string.Empty;
        FilesScanned   = 0;
        FoldersScanned = 0;
        BytesScanned   = "0 B";
        ErrorCount     = 0;
        ProgressValue  = 0;
        ProgressText   = "Preparing scan...";
        CurrentFile    = SelectedPath;

        _cts = new CancellationTokenSource();

        // Let the UI render the scanning state before the scanner starts heavy I/O work.
        await Task.Yield();

        var settings = await _settings.LoadAsync();
        var options = new ScanOptions
        {
            RootPath       = SelectedPath,
            MaxParallelism = Math.Clamp(settings.ScanParallelism, 1, 16),
            DbBatchSize    = 500,
            FollowSymlinks = false,
            DeepScan       = DeepScan,
            ExcludedPaths  = DeepScan ? [] : BuildExcludedPaths(settings),
        };

        // Capture the UI dispatcher before entering Task.Run so that progress
        // callbacks are always marshalled back to the UI thread, even if
        // SynchronizationContext is not installed (unpackaged WinUI 3).
        var dq = DispatcherQueue.GetForCurrentThread();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (dq is null || dq.HasThreadAccess)
                OnProgress(p);
            else
                dq.TryEnqueue(() => OnProgress(p));
        });

        // Use Turbo Scanner if the user has opted in and the binary is available.
        var activeScanner = (UseTurboScanner && TurboScannerAvailable)
            ? _turboScanner
            : _scanner;

        try
        {
            var session = await Task.Run(
                () => activeScanner.ScanAsync(options, progress, _cts.Token),
                _cts.Token);
            _lastSessionId = session.Id;
            ScanComplete   = true;
            ProgressText   = $"Scan complete — {ByteSizeConverter.Format(session.TotalSizeBytes)} in {session.TotalFiles:N0} files";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            HasError     = true;
            ErrorMessage = ex.Message;
            ProgressText = "Scan failed.";
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand]
    private void ViewResults()
    {
        if (_lastSessionId > 0)
            _nav.NavigateTo(typeof(ResultsPage), _lastSessionId);
    }

    private void OnProgress(ScanProgress p)
    {
        FilesScanned   = p.FilesScanned;
        FoldersScanned = p.FoldersScanned;
        BytesScanned   = ByteSizeConverter.Format(p.BytesScanned);
        ErrorCount     = p.ErrorCount;
        CurrentFile    = p.CurrentPath.Length > 80
            ? "…" + p.CurrentPath[^77..]
            : p.CurrentPath;
        ProgressText   = $"{ByteSizeConverter.Format(p.BytesScanned)} scanned · {p.FilesScanned:N0} files";

        // Drive info for progress bar (best-effort).
        var drive = _drives.GetDrive(SelectedPath);
        if (drive is { TotalBytes: > 0 })
            ProgressValue = (double)p.BytesScanned / drive.TotalBytes * 100.0;
    }

    private static IReadOnlyList<string> BuildExcludedPaths(AppSettings settings)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ScanOptions.DefaultExcludedPaths)
            excluded.Add(path);

        if (settings.SkipSystemFolders)
        {
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var systemX86Dir = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

            if (!string.IsNullOrWhiteSpace(windowsDir))
                excluded.Add(windowsDir);
            if (!string.IsNullOrWhiteSpace(systemDir))
                excluded.Add(systemDir);
            if (!string.IsNullOrWhiteSpace(systemX86Dir))
                excluded.Add(systemX86Dir);
        }

        foreach (var path in settings.ExcludedPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            excluded.Add(path);

        return excluded.ToArray();
    }
}
