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
    private long _initializationGeneration;
    private long _scanGeneration;
    private long _activeScanGeneration;

    [ObservableProperty] private string _selectedPath = @"C:\";
    [ObservableProperty] private bool _isInitializing;
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
    [ObservableProperty] private string _scanFileRate = "Calculating";
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
    public bool CanStartScan => !IsInitializing && !IsScanning && string.IsNullOrWhiteSpace(ScanPathError);
    public bool CanBrowse => !IsInitializing && !IsScanning;
    public bool CanCancel => IsScanning;

    partial void OnDeepScanChanged(bool value) => OnPropertyChanged(nameof(NeedsElevation));
    partial void OnSelectedPathChanged(string value) => ValidateSelectedPath(value);
    partial void OnIsInitializingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartScan));
        OnPropertyChanged(nameof(CanBrowse));
    }
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
    private long _lastProgressFiles;
    private long _estimatedScanBytes;
    private double _smoothedBytesPerSecond;
    private double _smoothedFilesPerSecond;

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

    public async Task InitializeAsync(
        bool autoEnableDeepScan = false,
        string? preselectedPath = null,
        CancellationToken cancellationToken = default)
    {
        if (IsScanning)
            return;

        var generation = Interlocked.Increment(ref _initializationGeneration);
        IsInitializing = true;
        try
        {
            var settings = await _settings.LoadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var availableDrives = _drives.GetAvailableDrives();

            // ScanViewModel is a singleton. A page that navigated away must not
            // overwrite a newer route, and initialization must never rewrite a
            // scan configuration after a scan has started.
            if (generation != Volatile.Read(ref _initializationGeneration) || IsScanning)
                return;

            AvailableDrives = availableDrives;
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
        finally
        {
            if (generation == Volatile.Read(ref _initializationGeneration))
                IsInitializing = false;
        }
    }

    [RelayCommand]
    private void SelectScanMode(string? mode)
    {
        if (!CanBrowse)
            return;

        SelectedScanMode = string.IsNullOrWhiteSpace(mode) ? "Standard Scan" : mode;
        DeepScan = SelectedScanMode == "Deep Scan";
        if (SelectedScanMode == "Quick Scan")
            UseTurboScanner = TurboScannerAvailable;
        ScanStepText = "Step 2 of 4 · Choose scan mode";
    }

    [RelayCommand]
    private void RequestElevation()
    {
        if (!CanBrowse)
            return;

        ValidateSelectedPath(SelectedPath);
        if (HasScanPathError)
        {
            HasError = true;
            ErrorMessage = ScanPathError;
            return;
        }

        var arguments = $"--cli scan --path {CommandLineArguments.Quote(SelectedPath)} --deep" +
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
        if (IsInitializing || IsScanning) return;

        ValidateSelectedPath(SelectedPath);
        if (!CanStartScan)
        {
            HasError = true;
            ErrorMessage = ScanPathError;
            return;
        }

        // Freeze every user-editable input before the first state change or
        // await. The displayed scope, estimate, scanner choice, and persisted
        // options must all describe the same scan.
        var rootPath = SelectedPath.Trim();
        var deepScan = DeepScan;
        var useTurboScanner = UseTurboScanner && TurboScannerAvailable;
        Interlocked.Increment(ref _initializationGeneration);
        var scanGeneration = Interlocked.Increment(ref _scanGeneration);
        Interlocked.Exchange(ref _activeScanGeneration, scanGeneration);
        var scanCancellation = new CancellationTokenSource();
        _cts = scanCancellation;

        IsScanning = true;
        ScanComplete = false;
        _lastSessionId = 0;
        HasError = false;
        ErrorMessage = string.Empty;
        FilesScanned = 0;
        FoldersScanned = 0;
        BytesScanned = "0 B";
        ErrorCount = 0;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        ProgressText = "Preparing scan...";
        CurrentFile = rootPath;
        ElapsedTime = "0s";
        ScanSpeed = "Calculating";
        ScanFileRate = "Calculating";
        EstimatedRemainingTime = "Calculating";
        ScanStepText = "Step 3 of 4 · Run scan";
        _scanStartedUtc = DateTime.UtcNow;
        _lastProgressUtc = _scanStartedUtc;
        _lastProgressBytes = 0;
        _lastProgressFiles = 0;
        // The drive-usage estimate only bounds the scan when the whole drive is scanned.
        _estimatedScanBytes = IsDriveRoot(rootPath) ? EstimateScanBytes(rootPath) : 0;
        _smoothedBytesPerSecond = 0;
        _smoothedFilesPerSecond = 0;

        try
        {
            // Let the UI render the scanning state before the scanner starts heavy I/O work.
            await Task.Yield();

            var settings = await _settings.LoadAsync(scanCancellation.Token);
            var options = new ScanOptions
            {
                RootPath = rootPath,
                MaxParallelism = Math.Clamp(settings.ScanParallelism, 1, 16),
                DbBatchSize = 500,
                FollowSymlinks = false,
                IncludeHiddenFiles = settings.ShowHiddenFiles || deepScan,
                DeepScan = deepScan,
                ExcludedPaths = ScanScopeResolver.BuildExcludedPaths(settings, deepScan),
            };

            // Capture the UI dispatcher before entering Task.Run so that progress
            // callbacks are always marshalled back to the UI thread, even if
            // SynchronizationContext is not installed (unpackaged WinUI 3).
            var dq = DispatcherQueue.GetForCurrentThread();
            var progress = new Progress<ScanProgress>(p =>
            {
                void ApplyIfCurrent()
                {
                    if (scanGeneration == Volatile.Read(ref _activeScanGeneration))
                        OnProgress(p);
                }

                if (dq is null || dq.HasThreadAccess)
                    ApplyIfCurrent();
                else
                    dq.TryEnqueue(ApplyIfCurrent);
            });

            var activeScanner = useTurboScanner ? _turboScanner : _scanner;
            var session = await Task.Run(
                () => activeScanner.ScanAsync(options, progress, scanCancellation.Token),
                scanCancellation.Token);
            Interlocked.CompareExchange(ref _activeScanGeneration, 0, scanGeneration);
            switch (session.Status)
            {
                case ScanStatus.Completed:
                    _lastSessionId = session.Id;
                    ScanComplete = true;
                    ScanStepText = "Step 4 of 4 · Review results";
                    ProgressText = $"Scan complete — {ByteSizeConverter.Format(session.TotalSizeBytes)} in {session.TotalFiles:N0} files";
                    break;
                case ScanStatus.Cancelled:
                    ScanComplete = false;
                    ProgressText = "Scan cancelled.";
                    ScanStepText = "Scan cancelled";
                    break;
                case ScanStatus.Failed:
                    ScanComplete = false;
                    HasError = true;
                    ErrorMessage = string.IsNullOrWhiteSpace(session.ErrorMessage)
                        ? "The scanner reported a failure without additional details."
                        : session.ErrorMessage;
                    ProgressText = "Scan failed.";
                    ScanStepText = "Scan failed";
                    break;
                default:
                    ScanComplete = false;
                    HasError = true;
                    ErrorMessage = $"The scanner returned non-terminal status {session.Status}.";
                    ProgressText = "Scan did not complete.";
                    ScanStepText = "Scan failed";
                    break;
            }
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
            ScanComplete = false;
            _lastSessionId = 0;
            ProgressText = "Scan cancelled.";
            ScanStepText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            ScanComplete = false;
            _lastSessionId = 0;
            HasError = true;
            ErrorMessage = ex.Message;
            ProgressText = "Scan failed.";
            ScanStepText = "Scan failed";
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeScanGeneration, 0, scanGeneration);
            IsScanning = false;
            if (ReferenceEquals(_cts, scanCancellation))
                _cts = null;
            scanCancellation.Dispose();
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

        // Rates are recomputed whenever time has advanced, not only when bytes
        // moved. A directory of many tiny files can add thousands of entries while
        // barely moving the byte counter, and the old byte-only guard left the
        // display frozen on a stale value during exactly those stretches.
        var sampleSeconds = (p.Timestamp - _lastProgressUtc).TotalSeconds;
        if (sampleSeconds >= 0.05)
        {
            var sampleBytes = Math.Max(0, p.BytesScanned - _lastProgressBytes);
            var sampleFiles = Math.Max(0, p.FilesScanned - _lastProgressFiles);

            _smoothedBytesPerSecond = Smooth(_smoothedBytesPerSecond, sampleBytes / sampleSeconds);
            _smoothedFilesPerSecond = Smooth(_smoothedFilesPerSecond, sampleFiles / sampleSeconds);

            ScanSpeed = $"{ByteSizeConverter.Format((long)_smoothedBytesPerSecond)}/s";
            ScanFileRate = FormatFileRate(_smoothedFilesPerSecond);

            _lastProgressUtc = p.Timestamp;
            _lastProgressBytes = p.BytesScanned;
            _lastProgressFiles = p.FilesScanned;
        }
        EstimatedRemainingTime = EstimateRemainingText(p.BytesScanned);

        CurrentFile = p.CurrentPath.Length > 80
            ? "…" + p.CurrentPath[^77..]
            : p.CurrentPath;
        ProgressText = $"{ByteSizeConverter.Format(p.BytesScanned)} scanned · {p.FilesScanned:N0} files";

        if (_estimatedScanBytes > 0)
        {
            ProgressValue = Math.Clamp((double)p.BytesScanned / _estimatedScanBytes * 100.0, 0.0, 100.0);
            IsProgressIndeterminate = false;
        }
        else
        {
            ProgressValue = 0;
            IsProgressIndeterminate = true;
        }
    }

    private static bool IsDriveRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) &&
                   string.Equals(
                       full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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

        var root = Path.GetPathRoot(candidate);
        if (!string.IsNullOrWhiteSpace(root) && !IsDriveReady(root))
        {
            ScanPathError = "Selected drive is not ready.";
            return;
        }

        if (ProbeAppDataWritable() is { } probeFailure)
        {
            ScanPathError = $"Startup preflight failed: {probeFailure}";
            return;
        }

        ScanPathError = string.Empty;
        ScanStepText = "Step 1 of 4 · Choose scope";
    }

    /// <summary>How long a readiness answer is trusted before the volume is asked again.</summary>
    private static readonly TimeSpan DriveReadinessTtl = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, (DateTime CheckedUtc, bool IsReady)> DriveReadinessCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Answers "is this volume attached?" from a short-lived cache.
    /// <para>
    /// <see cref="DriveInfo.IsReady"/> is the expensive half of path validation: on a
    /// stale mapped drive or a spun-down disk it blocks for seconds, and validation
    /// runs on the UI thread on every keystroke in the path box. A ten-second answer
    /// keeps typing responsive while still noticing a drive that was just plugged in
    /// or pulled out.
    /// </para>
    /// </summary>
    private static bool IsDriveReady(string root)
    {
        var now = DateTime.UtcNow;
        lock (DriveReadinessCache)
        {
            if (DriveReadinessCache.TryGetValue(root, out var cached) &&
                now - cached.CheckedUtc < DriveReadinessTtl)
            {
                return cached.IsReady;
            }
        }

        bool ready;
        try
        {
            ready = new DriveInfo(root).IsReady;
        }
        catch
        {
            // An unparseable or vanished root is simply not scannable.
            ready = false;
        }

        lock (DriveReadinessCache)
        {
            DriveReadinessCache[root] = (DateTime.UtcNow, ready);
        }

        return ready;
    }

    /// <summary>Set once the write probe has succeeded; failures are never cached.</summary>
    private static bool _appDataProbePassed;

    /// <summary>
    /// Checks that this install can write to its own AppData folder, at most once per
    /// process. Returns null when writable, otherwise the failure message.
    /// <para>
    /// The answer describes the install, not the path being validated, so repeating
    /// the create/write/delete on every keystroke was pure UI-thread disk I/O. Only
    /// success is remembered: after a failure the user may fix the permission and
    /// retype, and a cached failure would keep the scan blocked until restart.
    /// </para>
    /// </summary>
    private static string? ProbeAppDataWritable()
    {
        if (Volatile.Read(ref _appDataProbePassed))
            return null;

        try
        {
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
            return ex.Message;
        }

        Volatile.Write(ref _appDataProbePassed, true);
        return null;
    }

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

    /// <summary>
    /// Exponential moving average. The scanner reports every 300 ms and raw
    /// per-sample rates swing wildly between a folder of large media and a folder
    /// of source files, so the displayed figure is smoothed rather than raw.
    /// </summary>
    private static double Smooth(double previous, double sample) =>
        previous <= 0 ? sample : (previous * 0.75d) + (sample * 0.25d);

    /// <summary>
    /// Bytes per second is the wrong meter for trees of small files: throughput is
    /// bound by file count, so MB/s collapses while the scanner is in fact working
    /// hard. The file rate is shown alongside it, switching to per-minute when the
    /// per-second figure would round to zero.
    /// </summary>
    private static string FormatFileRate(double filesPerSecond)
    {
        if (filesPerSecond <= 0)
            return "Calculating";

        if (filesPerSecond < 1)
            return $"{filesPerSecond * 60:N0} files/min";

        return $"{filesPerSecond:N0} files/s";
    }

    private string EstimateRemainingText(long bytesScanned)
    {
        if (_smoothedBytesPerSecond <= 1)
            return "Estimating…";

        // An estimate is only offered when the scan covers a whole drive, because
        // that is the only case with a trustworthy total. Extrapolating a subtree
        // from drive usage produced figures like "10h 33m" that were pure noise.
        if (_estimatedScanBytes <= 0)
            return "No estimate for a subtree";

        var remainingBytes = _estimatedScanBytes - bytesScanned;
        if (remainingBytes <= 0)
            return "Finalizing";

        return FormatDuration(TimeSpan.FromSeconds(remainingBytes / _smoothedBytesPerSecond));
    }
}
