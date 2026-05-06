using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class ResultsViewModel : ObservableObject
{
    private const int FilePageSize = 200;
    private const int FolderPageSize = 100;
    private const int ErrorPageSize = 100;

    private readonly IScanRepository _repo;
    private readonly IScanErrorRepository _errorRepo;
    private readonly IScanResultDeletionService _resultDeletionService;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;
    private readonly DispatcherQueue _dispatcherQueue;

    private CancellationTokenSource? _filterDebounce;
    private CancellationTokenSource? _activeLoadCts;
    private CancellationTokenSource? _errorLoadCts;
    private CancellationTokenSource? _treeLoadCts;
    private int _loadedFileCount;
    private int _loadedFolderCount;
    private int _loadedErrorCount;
    private long _cachedSessionId;
    private bool _errorsLoaded;
    private bool _folderTreeLoaded;

    private string _fileSortColumn = "Size";
    private bool _fileSortDesc = true;
    private string _folderSortColumn = "Size";
    private bool _folderSortDesc = true;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isFilesLoading;
    [ObservableProperty] private bool _isFoldersLoading;
    [ObservableProperty] private bool _isCategoriesLoading;
    [ObservableProperty] private bool _isErrorsLoading;
    [ObservableProperty] private bool _isFolderTreeLoading;
    [ObservableProperty] private string _scanRoot = string.Empty;
    [ObservableProperty] private string _scanDate = string.Empty;
    [ObservableProperty] private string _totalSize = "—";
    [ObservableProperty] private long _totalFiles;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private string _filterCountLabel = string.Empty;
    [ObservableProperty] private long _totalFolderMatches;
    [ObservableProperty] private bool _canLoadMoreFiles;
    [ObservableProperty] private bool _canLoadMoreFolders;
    [ObservableProperty] private bool _canLoadMoreErrors;
    [ObservableProperty] private string _selectedCategoryFilter = string.Empty;
    [ObservableProperty] private string _sessionNote = string.Empty;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _errorsStatusText = "Open Errors to load scan issues.";
    [ObservableProperty] private string _folderTreeStatusText = "Open Folder Tree to load the hierarchy.";

    private long _sessionId;
    private List<FolderTreeNode> _folderTreeRoots = [];

    public ObservableCollection<FileEntry> LargestFiles { get; } = [];
    public ObservableCollection<FolderEntry> LargestFolders { get; } = [];
    public ObservableCollection<CategoryRow> CategoryBreakdown { get; } = [];
    public ObservableCollection<ScanError> ScanErrors { get; } = [];

    public IReadOnlyList<FolderTreeNode> FolderTreeRoots => _folderTreeRoots;

    public bool HasErrors => ErrorCount > 0;
    public bool HasCategoryFilter => !string.IsNullOrWhiteSpace(SelectedCategoryFilter);
    public bool HasSessionNote => !string.IsNullOrWhiteSpace(SessionNote);
    public bool HasCategoryBreakdown => CategoryBreakdown.Count > 0;
    public bool HasFolderTreeRoots => _folderTreeRoots.Count > 0;

    public string FileSizeHeader => "Size" + Indicator("Size", _fileSortColumn, _fileSortDesc);
    public string FileModifiedHeader => "Modified" + Indicator("Modified", _fileSortColumn, _fileSortDesc);
    public string FileTypeHeader => "Type" + Indicator("Type", _fileSortColumn, _fileSortDesc);
    public string FolderSizeHeader => "Total Size" + Indicator("Size", _folderSortColumn, _folderSortDesc);
    public string FolderFilesHeader => "Files" + Indicator("Files", _folderSortColumn, _folderSortDesc);

    public ResultsViewModel(
        IScanRepository repo,
        IScanErrorRepository errorRepo,
        IScanResultDeletionService resultDeletionService,
        INavigationService nav,
        IDialogService dialogs,
        DispatcherQueue? dispatcherQueue = null)
    {
        _repo = repo;
        _errorRepo = errorRepo;
        _resultDeletionService = resultDeletionService;
        _nav = nav;
        _dialogs = dialogs;
        _dispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
    }

    public async Task LoadMostRecentAsync()
    {
        var sessions = await _repo.GetRecentSessionsAsync(10);
        var latest = sessions.FirstOrDefault(static s => s.Status == ScanStatus.Completed);
        if (latest is not null)
            await LoadAsync(latest.Id);
        else
            ResetForNoSession();
    }

    public async Task LoadAsync(long sessionId)
    {
        if (sessionId <= 0)
        {
            ResetForNoSession();
            return;
        }

        if (_cachedSessionId == sessionId && HasSession)
            return;

        CancelBackgroundWork();
        _activeLoadCts = new CancellationTokenSource();
        var ct = _activeLoadCts.Token;

        ResetForSessionSwitch(sessionId);
        IsLoading = true;

        try
        {
            var session = await _repo.GetSessionAsync(sessionId, ct);
            if (session is null)
            {
                ResetForNoSession();
                return;
            }

            _cachedSessionId = sessionId;
            HasSession = true;
            ScanRoot = session.RootPath;
            ScanDate = session.CompletedUtc?.ToString("g") ?? session.StartedUtc.ToString("g");
            TotalSize = ByteSizeConverter.Format(session.TotalSizeBytes);
            TotalFiles = session.TotalFiles;
            SessionNote = session.ErrorMessage ?? string.Empty;

            await LoadPrimaryListsAsync(reset: true, ct);

            ErrorCount = (int)await _errorRepo.CountErrorsForSessionAsync(sessionId, ct);
            OnPropertyChanged(nameof(HasErrors));
            ErrorsStatusText = ErrorCount == 0
                ? "No scan errors were recorded for this session."
                : "Open Errors to load paged scan issues.";
            FolderTreeStatusText = "Open Folder Tree to build the hierarchy on demand.";

            _ = RunSecondaryLoadAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void CancelBackgroundWork()
    {
        _filterDebounce?.Cancel();
        _activeLoadCts?.Cancel();
        _errorLoadCts?.Cancel();
        _treeLoadCts?.Cancel();
    }

    public async Task EnsureErrorsLoadedAsync()
    {
        if (_errorsLoaded || _sessionId <= 0)
            return;

        _errorLoadCts?.Cancel();
        _errorLoadCts = new CancellationTokenSource();
        var ct = _errorLoadCts.Token;

        IsErrorsLoading = true;
        ErrorsStatusText = "Loading scan issues…";
        try
        {
            await LoadErrorsPageAsync(reset: true, ct);
            _errorsLoaded = true;
            ErrorsStatusText = ScanErrors.Count == 0
                ? "No scan issues were recorded for this session."
                : $"Loaded {ScanErrors.Count:N0} of {ErrorCount:N0} scan issue(s).";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsErrorsLoading = false;
        }
    }

    public async Task EnsureFolderTreeLoadedAsync()
    {
        if (_folderTreeLoaded || _sessionId <= 0)
            return;

        _treeLoadCts?.Cancel();
        _treeLoadCts = new CancellationTokenSource();
        var ct = _treeLoadCts.Token;

        IsFolderTreeLoading = true;
        FolderTreeStatusText = "Loading top-level folders…";
        try
        {
            var roots = await _repo.GetFolderTreeRootsAsync(_sessionId, ct);
            var nodes = new List<FolderTreeNode>(roots.Count);
            foreach (var root in roots)
            {
                var childCount = await _repo.CountFolderChildrenAsync(_sessionId, root.FullPath, ct);
                nodes.Add(new FolderTreeNode(root, childCount));
            }

            _folderTreeRoots = nodes;
            _folderTreeLoaded = true;
            FolderTreeStatusText = nodes.Count == 0
                ? "No folder rows are available for this session."
                : "Expand a folder to load only its direct children.";
            OnPropertyChanged(nameof(FolderTreeRoots));
            OnPropertyChanged(nameof(HasFolderTreeRoots));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsFolderTreeLoading = false;
        }
    }

    public async Task LoadFolderChildrenAsync(FolderTreeNode node)
    {
        if (node.AreChildrenLoaded || _sessionId <= 0)
            return;

        _treeLoadCts?.Cancel();
        _treeLoadCts = new CancellationTokenSource();
        var ct = _treeLoadCts.Token;

        IsFolderTreeLoading = true;
        node.IsLoadingChildren = true;
        FolderTreeStatusText = $"Loading children of {node.DisplayName}…";
        try
        {
            var children = await _repo.GetFolderChildrenAsync(_sessionId, node.Folder.FullPath, ct);
            var nodes = new List<FolderTreeNode>(children.Count);
            foreach (var child in children)
            {
                var childCount = await _repo.CountFolderChildrenAsync(_sessionId, child.FullPath, ct);
                nodes.Add(new FolderTreeNode(child, childCount));
            }

            node.SetChildren(nodes);
            FolderTreeStatusText = children.Count == 0
                ? $"{node.DisplayName} has no nested folders."
                : $"Loaded {children.Count:N0} child folder(s) for {node.DisplayName}.";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            node.IsLoadingChildren = false;
            IsFolderTreeLoading = false;
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce?.Cancel();
        _filterDebounce = new CancellationTokenSource();
        var token = _filterDebounce.Token;

        _ = Task.Delay(300, token).ContinueWith(
            _ => _dispatcherQueue.TryEnqueue(async () => await ApplyFilterAsync()),
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    partial void OnSelectedCategoryFilterChanged(string value) =>
        OnPropertyChanged(nameof(HasCategoryFilter));

    [RelayCommand]
    private async Task ClearFilterAsync()
    {
        _filterDebounce?.Cancel();
        FilterText = string.Empty;
        SelectedCategoryFilter = string.Empty;
        await ApplyFilterAsync();
    }

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

    [RelayCommand]
    private async Task DeleteSessionAsync()
    {
        if (_sessionId <= 0)
            return;

        var confirmed = await _dialogs.ConfirmAsync(
            "Delete this scan?",
            $"Permanently remove scan of \"{ScanRoot}\" ({ScanDate}) from history?\n\nThis cannot be undone.",
            "Delete");
        if (!confirmed)
            return;

        await _repo.DeleteSessionAsync(_sessionId);
        ResetForNoSession();
        _nav.NavigateTo(typeof(DashboardPage));
    }

    [RelayCommand]
    private void SortFilesBy(string column)
    {
        if (_fileSortColumn == column)
            _fileSortDesc = !_fileSortDesc;
        else
        {
            _fileSortColumn = column;
            _fileSortDesc = true;
        }

        RefreshFileSortHeaders();
        _ = ApplyFilterAsync();
    }

    [RelayCommand]
    private void SortFoldersBy(string column)
    {
        if (_folderSortColumn == column)
            _folderSortDesc = !_folderSortDesc;
        else
        {
            _folderSortColumn = column;
            _folderSortDesc = true;
        }

        RefreshFolderSortHeaders();
        _ = ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task DeleteFileAsync(FileEntry file)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Send to Recycle Bin?",
            $"Move \"{file.FileName}\" ({ByteSizeConverter.Format(file.SizeBytes)}) to the Recycle Bin?",
            "Send to Recycle Bin");
        if (!confirmed)
            return;

        var outcome = await _resultDeletionService.DeleteAsync(file, DeletionMethod.RecycleBin);
        if (!outcome.Success)
            return;

        LargestFiles.Remove(file);
        TotalFiles = Math.Max(0, TotalFiles - 1);
        _loadedFileCount = Math.Max(0, _loadedFileCount - 1);
        UpdateFilterCountLabel();
    }

    [RelayCommand]
    private async Task ApplyFilterAsync()
    {
        if (_sessionId <= 0)
            return;

        _activeLoadCts?.Cancel();
        _activeLoadCts = new CancellationTokenSource();
        var ct = _activeLoadCts.Token;

        try
        {
            await LoadPrimaryListsAsync(reset: true, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task FilterByCategoryAsync(CategoryRow row)
    {
        SelectedCategoryFilter = row.Category;
        await ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task ClearCategoryFilterAsync()
    {
        SelectedCategoryFilter = string.Empty;
        await ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task LoadMoreFilesAsync()
    {
        if (!CanLoadMoreFiles || _sessionId <= 0)
            return;

        _activeLoadCts?.Cancel();
        _activeLoadCts = new CancellationTokenSource();
        await ReloadFilesAsync(reset: false, _activeLoadCts.Token);
    }

    [RelayCommand]
    private async Task LoadMoreFoldersAsync()
    {
        if (!CanLoadMoreFolders || _sessionId <= 0)
            return;

        _activeLoadCts?.Cancel();
        _activeLoadCts = new CancellationTokenSource();
        await ReloadFoldersAsync(reset: false, _activeLoadCts.Token);
    }

    [RelayCommand]
    private async Task LoadMoreErrorsAsync()
    {
        if (!CanLoadMoreErrors || _sessionId <= 0)
            return;

        _errorLoadCts?.Cancel();
        _errorLoadCts = new CancellationTokenSource();
        await LoadErrorsPageAsync(reset: false, _errorLoadCts.Token);
    }

    private async Task RunSecondaryLoadAsync(CancellationToken ct)
    {
        IsCategoriesLoading = true;
        try
        {
            var breakdown = await _repo.GetCategoryBreakdownAsync(_sessionId, ct);
            CategoryBreakdown.Clear();
            foreach (var (cat, (count, bytes)) in breakdown.OrderByDescending(static x => x.Value.Bytes))
                CategoryBreakdown.Add(new CategoryRow(cat.ToString(), count, ByteSizeConverter.Format(bytes)));

            OnPropertyChanged(nameof(HasCategoryBreakdown));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsCategoriesLoading = false;
        }
    }

    private async Task LoadPrimaryListsAsync(bool reset, CancellationToken ct)
    {
        var fileTask = ReloadFilesAsync(reset, ct);
        var folderTask = ReloadFoldersAsync(reset, ct);
        await Task.WhenAll(fileTask, folderTask);
    }

    private async Task ReloadFilesAsync(bool reset, CancellationToken ct)
    {
        if (_sessionId <= 0)
            return;

        IsFilesLoading = true;
        try
        {
            var offset = reset ? 0 : _loadedFileCount;
            var page = await _repo.SearchFilesAsync(
                _sessionId,
                FilterText.Trim(),
                SelectedCategoryFilter,
                _fileSortColumn,
                _fileSortDesc,
                offset,
                FilePageSize,
                ct);

            if (reset)
            {
                LargestFiles.Clear();
                _loadedFileCount = 0;
            }

            foreach (var file in page)
                LargestFiles.Add(file);

            _loadedFileCount += page.Count;
            TotalFiles = await _repo.CountFilesAsync(_sessionId, FilterText.Trim(), SelectedCategoryFilter, ct);
            CanLoadMoreFiles = _loadedFileCount < TotalFiles;
            UpdateFilterCountLabel();
        }
        finally
        {
            IsFilesLoading = false;
        }
    }

    private async Task ReloadFoldersAsync(bool reset, CancellationToken ct)
    {
        if (_sessionId <= 0)
            return;

        IsFoldersLoading = true;
        try
        {
            var offset = reset ? 0 : _loadedFolderCount;
            var page = await _repo.SearchFoldersAsync(
                _sessionId,
                FilterText.Trim(),
                _folderSortColumn,
                _folderSortDesc,
                offset,
                FolderPageSize,
                ct);

            if (reset)
            {
                LargestFolders.Clear();
                _loadedFolderCount = 0;
            }

            foreach (var folder in page)
                LargestFolders.Add(folder);

            _loadedFolderCount += page.Count;
            TotalFolderMatches = await _repo.CountFoldersAsync(_sessionId, FilterText.Trim(), ct);
            CanLoadMoreFolders = _loadedFolderCount < TotalFolderMatches;
        }
        finally
        {
            IsFoldersLoading = false;
        }
    }

    private async Task LoadErrorsPageAsync(bool reset, CancellationToken ct)
    {
        if (_sessionId <= 0)
            return;

        var offset = reset ? 0 : _loadedErrorCount;
        var page = await _errorRepo.GetErrorsPageForSessionAsync(_sessionId, offset, ErrorPageSize, ct);

        if (reset)
        {
            ScanErrors.Clear();
            _loadedErrorCount = 0;
        }

        foreach (var error in page)
            ScanErrors.Add(error);

        _loadedErrorCount += page.Count;
        CanLoadMoreErrors = _loadedErrorCount < ErrorCount;
        OnPropertyChanged(nameof(CanLoadMoreErrors));
    }

    private void ResetForSessionSwitch(long sessionId)
    {
        _sessionId = sessionId;
        HasSession = false;
        CategoryBreakdown.Clear();
        ScanErrors.Clear();
        LargestFiles.Clear();
        LargestFolders.Clear();
        _folderTreeRoots = [];
        _loadedFileCount = 0;
        _loadedFolderCount = 0;
        _loadedErrorCount = 0;
        _errorsLoaded = false;
        _folderTreeLoaded = false;
        ErrorCount = 0;
        CanLoadMoreFiles = false;
        CanLoadMoreFolders = false;
        CanLoadMoreErrors = false;
        ErrorsStatusText = "Open Errors to load scan issues.";
        FolderTreeStatusText = "Open Folder Tree to load the hierarchy.";
        OnPropertyChanged(nameof(FolderTreeRoots));
        OnPropertyChanged(nameof(HasFolderTreeRoots));
        OnPropertyChanged(nameof(HasCategoryBreakdown));
    }

    private void ResetForNoSession()
    {
        CancelBackgroundWork();
        _sessionId = 0;
        _cachedSessionId = 0;
        ResetForSessionSwitch(0);
        ScanRoot = string.Empty;
        ScanDate = string.Empty;
        TotalSize = "—";
        TotalFiles = 0;
        SessionNote = string.Empty;
        FilterCountLabel = "No completed scan sessions yet.";
        HasSession = false;
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

    private void UpdateFilterCountLabel()
    {
        FilterCountLabel = HasCategoryFilter
            ? $"Showing {LargestFiles.Count:N0} of {TotalFiles:N0} files in {SelectedCategoryFilter}."
            : $"Showing {LargestFiles.Count:N0} of {TotalFiles:N0} files.";
    }

    private static string Indicator(string col, string current, bool desc) =>
        current == col ? (desc ? " ▼" : " ▲") : string.Empty;
}

public sealed record CategoryRow(string Category, long FileCount, string TotalSize);
