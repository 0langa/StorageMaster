using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class ScanViewModel : ObservableObject
{
    private readonly IFileScanner       _scanner;
    private readonly IDriveInfoProvider _drives;
    private readonly INavigationService _nav;

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

    private long _lastSessionId;

    public ScanViewModel(
        IFileScanner       scanner,
        IDriveInfoProvider drives,
        INavigationService nav)
    {
        _scanner = scanner;
        _drives  = drives;
        _nav     = nav;
    }

    public void Initialize()
    {
        AvailableDrives = _drives.GetAvailableDrives();
        ScanComplete    = false;
        HasError        = false;
        ErrorMessage    = string.Empty;
    }

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

        _cts = new CancellationTokenSource();

        var options = new ScanOptions
        {
            RootPath       = SelectedPath,
            MaxParallelism = 4,
            DbBatchSize    = 500,
            FollowSymlinks = false,
        };

        var progress = new Progress<ScanProgress>(OnProgress);

        try
        {
            var session = await _scanner.ScanAsync(options, progress, _cts.Token);
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
}
