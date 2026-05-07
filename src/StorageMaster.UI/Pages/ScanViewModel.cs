using CommunityToolkit.Mvvm.ComponentModel;
using StorageMaster.Platform.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class ScanViewModel : ObservableObject
{
    private readonly IFileScanner _scanner;
    private readonly IFileScanner _turboScanner;
    private readonly IDriveInfoProvider _drives;
    private readonly INavigationService _nav;
    private readonly IAdminService _admin;
    private readonly ISettingsRepository _settings;

    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _selectedPath = @"C:\";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _scanComplete;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _currentFile = string.Empty;
    [ObservableProperty] private long _filesScanned;
    [ObservableProperty] private long _foldersScanned;
    [ObservableProperty] private string _bytesScanned = "0 B";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _scanPathError = string.Empty;
    [ObservableProperty] private IReadOnlyList<DriveDetail> _availableDrives = [];
    [ObservableProperty] private bool _deepScan;
    [ObservableProperty] private bool _useTurboScanner;
    [ObservableProperty] private bool _turboScannerAvailable;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private string _selectedScanMode = "Standard Scan";
    [ObservableProperty] private string _elapsedTime = "0s";
    [ObservableProperty] private string _scanSpeed = "Calculating";
    [ObservableProperty] private string _estimatedRemainingTime = "Calculating";
    [ObservableProperty] private string _scanStepText = "Step 1 of 4 · Choose scope";

    /// <summary>True when the process already holds administrator privileges.</summary>
    public bool IsRunningAsAdmin => _admin.IsRunningAsAdmin;

    /// <summary>
    /// True when deep scan is on but we are NOT running as admin —
    /// the user should be prompted to elevate.
    /// </summary>
    public bool NeedsElevation => DeepScan && !IsRunningAsAdmin;
    public bool HasScanPathError => !string.IsNullOrWhiteSpace(ScanPathError);
    public bool CanStartScan => !IsScanning && string.IsNullOrWhiteSpace(ScanPathError);
    public bool CanBrowse => !IsScanning;
    public bool CanCancel => IsScanning;

    partial void OnDeepScanChanged(bool value) => OnPropertyChanged(nameof(NeedsElevation));
    partial void OnSelectedPathChanged(string value) => ValidateSelectedPath(value);
    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartScan));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(CanCancel));
    }
    partial void OnScanPathErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasScanPathError));
        OnPropertyChanged(nameof(CanStartScan));
    }

    private long _lastSessionId;
    private DateTime _scanStartedUtc;
    private DateTime _lastProgressUtc;
    private long _lastProgressBytes;
    private long _estimatedScanBytes;
    private double _smoothedBytesPerSecond;

    public ScanViewModel(
        IFileScanner scanner,
        IFileScanner turboScanner,
        IDriveInfoProvider drives,
        INavigationService nav,
        IAdminService admin,
        ISettingsRepository settings)
    {
        _scanner = scanner;
        _turboScanner = turboScanner;
        _drives = drives;
        _nav = nav;
        _admin = admin;
        _settings = settings;
    }

    public async Task InitializeAsync(bool autoEnableDeepScan = false, string? preselectedPath = null)
    {
        var settings = await _settings.LoadAsync();
        AvailableDrives = _drives.GetAvailableDrives();
        ScanComplete = false;
        HasError = false;
        ErrorMessage = string.Empty;
        SelectedPath = string.IsNullOrWhiteSpace(preselectedPath)
            ? (string.IsNullOrWhiteSpace(settings.DefaultScanPath) ? @"C:\" : settings.DefaultScanPath)
            : preselectedPath;
        UseTurboScanner = settings.UseTurboScanner;
        TurboScannerAvailable = StorageMaster.Platform.Windows.TurboFileScanner.IsAvailable;
        if (autoEnableDeepScan)
            DeepScan = true;
        ValidateSelectedPath(SelectedPath);
    }

    [RelayCommand]
    private void SelectScanMode(string? mode)
    {
        SelectedScanMode = string.IsNullOrWhiteSpace(mode) ? "Standard Scan" : mode;
        DeepScan = SelectedScanMode == "Deep Scan";
        if (SelectedScanMode == "Quick Scan")
            UseTurboScanner = TurboScannerAvailable;
        ScanStepText = "Step 2 of 4 · Choose scan mode";
    }

    [RelayCommand]
    private void RequestElevation()
    {
        ValidateSelectedPath(SelectedPath);
        if (HasScanPathError)
        {
            HasError = true;
            ErrorMessage = ScanPathError;
            return;
        }

        var arguments = $"--cli scan --path {QuoteArgument(SelectedPath)} --deep" +
                        (UseTurboScanner && TurboScannerAvailable ? " --turbo" : string.Empty);
        if (_admin.TryStartElevated(arguments))
        {
            HasError = false;
            ErrorMessage = string.Empty;
            ProgressText = "Elevated deep scan started in a separate command-line worker. Refresh Results when it completes.";
        }
        else
        {
            HasError = true;
            ErrorMessage = "Could not start the elevated scan worker.";
        }
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (IsScanning) return;

        ValidateSelectedPath(SelectedPath);
        if (!CanStartScan)
        {
            HasError = true;
            ErrorMessage = ScanPathError;
            return;
        }

        IsScanning = true;
        ScanComplete = false;
        HasError = false;
        ErrorMessage = string.Empty;
        FilesScanned = 0;
        FoldersScanned = 0;
        BytesScanned = "0 B";
        ErrorCount = 0;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        ProgressText = "Preparing scan...";
        CurrentFile = SelectedPath;
        ElapsedTime = "0s";
        ScanSpeed = "Calculating";
        EstimatedRemainingTime = "Calculating";
        ScanStepText = "Step 3 of 4 · Run scan";
        _scanStartedUtc = DateTime.UtcNow;
        _lastProgressUtc = _scanStartedUtc;
        _lastProgressBytes = 0;
        _estimatedScanBytes = EstimateScanBytes(SelectedPath);
        _smoothedBytesPerSecond = 0;

        _cts = new CancellationTokenSource();

        // Let the UI render the scanning state before the scanner starts heavy I/O work.
        await Task.Yield();

        var settings = await _settings.LoadAsync();
        var options = new ScanOptions
        {
            RootPath = SelectedPath,
            MaxParallelism = Math.Clamp(settings.ScanParallelism, 1, 16),
            DbBatchSize = 500,
            FollowSymlinks = false,
            IncludeHiddenFiles = settings.ShowHiddenFiles || DeepScan,
            DeepScan = DeepScan,
            ExcludedPaths = ScanScopeResolver.BuildExcludedPaths(settings, DeepScan),
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
            ScanComplete = true;
            ScanStepText = "Step 4 of 4 · Review results";
            ProgressText = $"Scan complete — {ByteSizeConverter.Format(session.TotalSizeBytes)} in {session.TotalFiles:N0} files";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Scan cancelled.";
            ScanStepText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            ProgressText = "Scan failed.";
            ScanStepText = "Scan failed";
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
            _nav.NavigateTo(typeof(ScanWorkspacePage), _lastSessionId);
    }

    private void OnProgress(ScanProgress p)
    {
        var elapsed = p.Timestamp - _scanStartedUtc;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        FilesScanned = p.FilesScanned;
        FoldersScanned = p.FoldersScanned;
        BytesScanned = ByteSizeConverter.Format(p.BytesScanned);
        ErrorCount = p.ErrorCount;
        ElapsedTime = FormatDuration(elapsed);

        var sampleSeconds = Math.Max(0.001, (p.Timestamp - _lastProgressUtc).TotalSeconds);
        var sampleBytes = p.BytesScanned - _lastProgressBytes;
        if (sampleBytes > 0)
        {
            var currentBytesPerSecond = sampleBytes / sampleSeconds;
            _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0
                ? currentBytesPerSecond
                : (_smoothedBytesPerSecond * 0.75d) + (currentBytesPerSecond * 0.25d);
            ScanSpeed = $"{ByteSizeConverter.Format((long)_smoothedBytesPerSecond)}/s";
        }

        _lastProgressUtc = p.Timestamp;
        _lastProgressBytes = p.BytesScanned;
        EstimatedRemainingTime = EstimateRemainingText(p.BytesScanned);

        CurrentFile = p.CurrentPath.Length > 80
            ? "…" + p.CurrentPath[^77..]
            : p.CurrentPath;
        ProgressText = $"{ByteSizeConverter.Format(p.BytesScanned)} scanned · {p.FilesScanned:N0} files";
        ProgressValue = 0;
        IsProgressIndeterminate = true;
    }

    private void ValidateSelectedPath(string? path)
    {
        if (IsScanning)
            return;

        var candidate = path?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            ScanPathError = "Choose a folder or drive to scan.";
            return;
        }

        if (!Path.IsPathRooted(candidate))
        {
            ScanPathError = "Scan path must be an absolute Windows path.";
            return;
        }

        if (!Directory.Exists(candidate))
        {
            ScanPathError = "Scan path does not exist or is not currently available.";
            return;
        }

        try
        {
            var root = Path.GetPathRoot(candidate);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    ScanPathError = "Selected drive is not ready.";
                    return;
                }
            }

            var appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StorageMaster");
            Directory.CreateDirectory(appDataRoot);
            var probePath = Path.Combine(appDataRoot, ".write-test");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
        }
        catch (Exception ex)
        {
            ScanPathError = $"Startup preflight failed: {ex.Message}";
            return;
        }

        ScanPathError = string.Empty;
        ScanStepText = "Step 1 of 4 · Choose scope";
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        if (value.TotalMinutes >= 1)
            return $"{value.Minutes}m {value.Seconds}s";
        return $"{Math.Max(0, value.Seconds)}s";
    }

    private static long EstimateScanBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
                return 0;

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return 0;

            return Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
        }
        catch
        {
            return 0;
        }
    }

    private string EstimateRemainingText(long bytesScanned)
    {
        if (_smoothedBytesPerSecond <= 1)
            return "Waiting for data";

        if (_estimatedScanBytes <= 0)
            return "Unknown total";

        var remainingBytes = _estimatedScanBytes - bytesScanned;
        if (remainingBytes <= 0)
            return "Finalizing";

        return FormatDuration(TimeSpan.FromSeconds(remainingBytes / _smoothedBytesPerSecond));
    }
}
