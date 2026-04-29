using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class ResultsViewModel : ObservableObject
{
    private readonly IScanRepository      _repo;
    private readonly IScanErrorRepository _errorRepo;
    private readonly IFileDeleter         _deleter;
    private readonly INavigationService   _nav;
    private readonly DispatcherQueue      _dispatcherQueue;

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _scanRoot    = string.Empty;
    [ObservableProperty] private string _scanDate    = string.Empty;
    [ObservableProperty] private string _totalSize   = "—";
    [ObservableProperty] private long   _totalFiles;
    [ObservableProperty] private string _filterText  = string.Empty;
    [ObservableProperty] private int    _errorCount;

    private long _sessionId;
    private IReadOnlyList<FileEntry>   _allFiles   = [];
    private IReadOnlyList<FolderEntry> _allFolders = [];

    // ── Sort state ──────────────────────────────────────────────────────────
    // Files
    private string _fileSortColumn    = "Size";
    private bool   _fileSortDesc      = true;
    // Folders
    private string _folderSortColumn  = "Size";
    private bool   _folderSortDesc    = true;

    // Sort-indicator headers — updated by the sort commands below.
    public string FileSizeHeader      => "Size"       + Indicator("Size",     _fileSortColumn,   _fileSortDesc);
    public string FileModifiedHeader  => "Modified"   + Indicator("Modified", _fileSortColumn,   _fileSortDesc);
    public string FileTypeHeader      => "Type"       + Indicator("Type",     _fileSortColumn,   _fileSortDesc);
    public string FolderSizeHeader    => "Total Size" + Indicator("Size",     _folderSortColumn, _folderSortDesc);
    public string FolderFilesHeader   => "Files"      + Indicator("Files",    _folderSortColumn, _folderSortDesc);

    private static string Indicator(string col, string current, bool desc) =>
        current == col ? (desc ? " ▼" : " ▲") : "";

    // ── XamlRoot for ContentDialogs ──────────────────────────────────────────
    /// <summary>Set by ResultsPage.OnNavigatedTo so the ViewModel can show dialogs.</summary>
    public XamlRoot? XamlRoot { get; set; }

    public ObservableCollection<FileEntry>   LargestFiles      { get; } = [];
    public ObservableCollection<FolderEntry> LargestFolders    { get; } = [];
    public ObservableCollection<CategoryRow> CategoryBreakdown { get; } = [];
    public ObservableCollection<ScanError>   ScanErrors        { get; } = [];

    public bool HasErrors => ErrorCount > 0;

    public ResultsViewModel(
        IScanRepository      repo,
        IScanErrorRepository errorRepo,
        IFileDeleter         deleter,
        INavigationService   nav)
    {
        _repo            = repo;
        _errorRepo       = errorRepo;
        _deleter         = deleter;
        _nav             = nav;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Loads the most recent completed session. Called when the Results tab is
    /// opened without a specific session ID (i.e. not via "View Results" button).
    /// </summary>
    public async Task LoadMostRecentAsync()
    {
        var sessions = await _repo.GetRecentSessionsAsync(10);
        var latest   = sessions.FirstOrDefault(s => s.Status == Core.Models.ScanStatus.Completed);
        if (latest is not null)
            await LoadAsync(latest.Id);
    }

    public async Task LoadAsync(long sessionId)
    {
        _sessionId = sessionId;
        IsLoading  = true;

        try
        {
            var session = await _repo.GetSessionAsync(sessionId);
            if (session is not null)
            {
                ScanRoot   = session.RootPath;
                ScanDate   = session.CompletedUtc?.ToString("g") ?? session.StartedUtc.ToString("g");
                TotalSize  = ByteSizeConverter.Format(session.TotalSizeBytes);
                TotalFiles = session.TotalFiles;
            }

            _allFiles   = await _repo.GetLargestFilesAsync(sessionId,   topN: 500);
            _allFolders = await _repo.GetLargestFoldersAsync(sessionId,  topN: 200);

            ApplyFilter();

            var breakdown = await _repo.GetCategoryBreakdownAsync(sessionId);
            CategoryBreakdown.Clear();
            foreach (var (cat, (count, bytes)) in breakdown.OrderByDescending(x => x.Value.Bytes))
            {
                CategoryBreakdown.Add(new CategoryRow(cat.ToString(), count, ByteSizeConverter.Format(bytes)));
            }

            var errors = await _errorRepo.GetErrorsForSessionAsync(sessionId);
            ScanErrors.Clear();
            foreach (var e in errors) ScanErrors.Add(e);
            ErrorCount = ScanErrors.Count;
            OnPropertyChanged(nameof(HasErrors));
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private static void OpenInExplorer(FileEntry file)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\"")
        {
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private static void OpenFolderInExplorer(FolderEntry folder)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.FullPath}\"")
        {
            UseShellExecute = true
        });
    }

    // ── Copy path ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private static void CopyFilePath(FileEntry file)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(file.FullPath);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    [RelayCommand]
    private static void CopyFolderPath(FolderEntry folder)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(folder.FullPath);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    // ── Delete session ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DeleteSessionAsync()
    {
        if (XamlRoot is null || _sessionId <= 0) return;

        var dialog = new ContentDialog
        {
            Title             = "Delete this scan?",
            Content           = $"Permanently remove the scan of \"{ScanRoot}\" ({ScanDate}) from history?\n\nThis cannot be undone.",
            PrimaryButtonText  = "Delete",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        await Task.Run(() => _repo.DeleteSessionAsync(_sessionId));
        _nav.NavigateTo(typeof(DashboardPage));
    }

    // ── Sort commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void SortFilesBy(string column)
    {
        if (_fileSortColumn == column)
            _fileSortDesc = !_fileSortDesc;
        else
        {
            _fileSortColumn = column;
            _fileSortDesc   = true;
        }
        RefreshFileSortHeaders();
        ApplyFilter();
    }

    [RelayCommand]
    private void SortFoldersBy(string column)
    {
        if (_folderSortColumn == column)
            _folderSortDesc = !_folderSortDesc;
        else
        {
            _folderSortColumn = column;
            _folderSortDesc   = true;
        }
        RefreshFolderSortHeaders();
        ApplyFilter();
    }

    private void RefreshFileSortHeaders()
    {
        OnPropertyChanged(nameof(FileSizeHeader));
        OnPropertyChanged(nameof(FileModifiedHeader));
        OnPropertyChanged(nameof(FileTypeHeader));
    }

    private void RefreshFolderSortHeaders()
    {
        OnPropertyChanged(nameof(FolderSizeHeader));
        OnPropertyChanged(nameof(FolderFilesHeader));
    }

    // ── Delete command ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DeleteFileAsync(FileEntry file)
    {
        if (XamlRoot is null) return;

        var dialog = new ContentDialog
        {
            Title             = "Send to Recycle Bin?",
            Content           = $"Move \"{file.FileName}\" ({ByteSizeConverter.Format(file.SizeBytes)}) to the Recycle Bin?",
            PrimaryButtonText  = "Send to Recycle Bin",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        var outcome = await Task.Run(() => _deleter.DeleteAsync(
            new DeletionRequest(file.FullPath, DeletionMethod.RecycleBin, DryRun: false)));

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (outcome.Success)
            {
                LargestFiles.Remove(file);
                // Prune from the backing list so re-filtering doesn't resurrect it.
                _allFiles = _allFiles.Where(f => f.FullPath != file.FullPath).ToList();
            }
            // On failure: leave the item in the list; the user can retry or investigate.
        });
    }

    // ── Filter + Sort ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void ApplyFilter()
    {
        var filter = FilterText.Trim();

        // ── Files ──
        IEnumerable<FileEntry> filteredFiles = _allFiles;
        if (!string.IsNullOrEmpty(filter))
            filteredFiles = filteredFiles.Where(f =>
                f.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase));

        filteredFiles = _fileSortColumn switch
        {
            "Modified" => _fileSortDesc
                ? filteredFiles.OrderByDescending(f => f.ModifiedUtc)
                : filteredFiles.OrderBy(f => f.ModifiedUtc),
            "Type"     => _fileSortDesc
                ? filteredFiles.OrderByDescending(f => f.Category)
                : filteredFiles.OrderBy(f => f.Category),
            _          => _fileSortDesc          // "Size" (default)
                ? filteredFiles.OrderByDescending(f => f.SizeBytes)
                : filteredFiles.OrderBy(f => f.SizeBytes),
        };

        LargestFiles.Clear();
        foreach (var f in filteredFiles.Take(200))
            LargestFiles.Add(f);

        // ── Folders ──
        IEnumerable<FolderEntry> filteredFolders = _allFolders;
        if (!string.IsNullOrEmpty(filter))
            filteredFolders = filteredFolders.Where(f =>
                f.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase));

        filteredFolders = _folderSortColumn switch
        {
            "Files" => _folderSortDesc
                ? filteredFolders.OrderByDescending(f => f.FileCount)
                : filteredFolders.OrderBy(f => f.FileCount),
            _       => _folderSortDesc           // "Size" (default)
                ? filteredFolders.OrderByDescending(f => f.TotalSizeBytes)
                : filteredFolders.OrderBy(f => f.TotalSizeBytes),
        };

        LargestFolders.Clear();
        foreach (var f in filteredFolders.Take(100))
            LargestFolders.Add(f);
    }
}

public sealed record CategoryRow(string Category, long FileCount, string TotalSize);
