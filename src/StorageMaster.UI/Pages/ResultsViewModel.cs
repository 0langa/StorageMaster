using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private readonly record struct PrimaryLoadOperation(
        long SessionVersion,
        long LoadVersion,
        long SessionId,
        CancellationToken Token);

    private readonly IScanRepository _repo;
    private readonly IScanErrorRepository _errorRepo;
    private readonly IScanResultDeletionService _resultDeletionService;
    private readonly INavigationService _nav;
    private readonly IDialogService _dialogs;

    private CancellationTokenSource? _filterDebounce;
    private CancellationTokenSource? _sessionLoadCts;
    private CancellationTokenSource? _primaryLoadCts;
    private CancellationTokenSource? _categoryLoadCts;
    private CancellationTokenSource? _errorLoadCts;
    private Task? _errorsLoadTask;
    private Task? _folderTreeLoadTask;
    private int _loadedFileCount;
    private int _loadedFolderCount;
    private int _loadedErrorCount;
    private long _sessionLoadVersion;
    private long _primaryLoadVersion;
    private long _errorLoadVersion;
    private int _activeTreeLoads;
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

    /// <summary>
    /// Raised on the UI thread after a category filter is applied from the File Types tab,
    /// so the page can switch the pivot to Largest Files to show the filtered results.
    /// </summary>
    public event EventHandler? CategoryFilterApplied;

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
        IDialogService dialogs)
    {
        _repo = repo;
        _errorRepo = errorRepo;
        _resultDeletionService = resultDeletionService;
        _nav = nav;
        _dialogs = dialogs;
    }

    public async Task LoadMostRecentAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await _repo.GetRecentSessionsAsync(10, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var latest = sessions.FirstOrDefault(static s => s.Status == ScanStatus.Completed);
        if (latest is not null)
            await LoadAsync(latest.Id, cancellationToken);
        else
            ResetForNoSession();
    }

    public async Task LoadAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId <= 0)
        {
            ResetForNoSession();
            return;
        }

        CancelBackgroundWork();
        var sessionVersion = ++_sessionLoadVersion;
        _sessionLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _sessionLoadCts.Token;

        ResetForSessionSwitch(sessionId);
        IsLoading = true;
        await Task.Yield();

        try
        {
            var session = await _repo.GetSessionAsync(sessionId, ct);
            if (session is null)
            {
                if (IsCurrentSession(sessionVersion, sessionId, ct))
                    ResetForNoSession();
                return;
            }

            ct.ThrowIfCancellationRequested();
            if (!IsCurrentSession(sessionVersion, sessionId, ct))
                return;

            HasSession = true;
            ScanRoot = session.RootPath;
            ScanDate = session.CompletedUtc?.ToString("g") ?? session.StartedUtc.ToString("g");
            TotalSize = ByteSizeConverter.Format(session.TotalSizeBytes);
            TotalFiles = session.TotalFiles;
            SessionNote = session.ErrorMessage ?? string.Empty;

            await LoadPrimaryListsAsync(reset: true, ct);

            var errorCount = (int)await _errorRepo.CountErrorsForSessionAsync(sessionId, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentSession(sessionVersion, sessionId, ct))
                return;

            ErrorCount = errorCount;
            OnPropertyChanged(nameof(HasErrors));
            ErrorsStatusText = ErrorCount == 0
                ? "No scan errors were recorded for this session."
                : "Open Errors to load paged scan issues.";
            FolderTreeStatusText = "Open Folder Tree to build the hierarchy on demand.";

            StartSecondaryLoad(sessionVersion, sessionId, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionVersion, sessionId, ct))
                FilterCountLabel = $"Could not load scan results: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            if (IsCurrentSession(sessionVersion, sessionId))
                IsLoading = false;
        }
    }

    public void CancelBackgroundWork()
    {
        _sessionLoadVersion++;
        _primaryLoadVersion++;
        _errorLoadVersion++;
        CancelAndDispose(ref _filterDebounce);
        CancelAndDispose(ref _categoryLoadCts);
        CancelAndDispose(ref _primaryLoadCts);
        CancelAndDispose(ref _errorLoadCts);
        CancelAndDispose(ref _sessionLoadCts);
        _errorsLoadTask = null;
        _folderTreeLoadTask = null;
        _activeTreeLoads = 0;
        IsLoading = false;
        IsFilesLoading = false;
        IsFoldersLoading = false;
        IsCategoriesLoading = false;
        IsErrorsLoading = false;
        IsFolderTreeLoading = false;
    }

    public Task EnsureErrorsLoadedAsync()
    {
        if (_errorsLoaded || _sessionId <= 0)
            return Task.CompletedTask;

        if (_errorsLoadTask is { IsCompleted: false })
            return _errorsLoadTask;

        var sessionVersion = _sessionLoadVersion;
        var sessionId = _sessionId;
        var loadVersion = ++_errorLoadVersion;
        CancelAndDispose(ref _errorLoadCts);
        _errorLoadCts = CreateSessionLinkedTokenSource();
        _errorsLoadTask = EnsureErrorsLoadedCoreAsync(
            sessionVersion,
            sessionId,
            loadVersion,
            _errorLoadCts.Token);
        return _errorsLoadTask;
    }

    private async Task EnsureErrorsLoadedCoreAsync(
        long sessionVersion,
        long sessionId,
        long loadVersion,
        CancellationToken ct)
    {
        IsErrorsLoading = true;
        ErrorsStatusText = "Loading scan issues…";

        try
        {
            var page = await _errorRepo.GetErrorsPageForSessionAsync(
                sessionId,
                0,
                ErrorPageSize,
                ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentErrorLoad(sessionVersion, sessionId, loadVersion, ct))
                return;

            ScanErrors.Clear();
            foreach (var error in page)
                ScanErrors.Add(error);
            _loadedErrorCount = page.Count;
            CanLoadMoreErrors = _loadedErrorCount < ErrorCount;
            _errorsLoaded = true;
            ErrorsStatusText = ScanErrors.Count == 0
                ? "No scan issues were recorded for this session."
                : $"Loaded {ScanErrors.Count:N0} of {ErrorCount:N0} scan issue(s).";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentErrorLoad(sessionVersion, sessionId, loadVersion))
                ErrorsStatusText = $"Could not load scan issues: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            if (IsCurrentErrorLoad(sessionVersion, sessionId, loadVersion))
            {
                IsErrorsLoading = false;
                _errorsLoadTask = null;
            }
        }
    }

    public Task EnsureFolderTreeLoadedAsync()
    {
        if (_folderTreeLoaded || _sessionId <= 0)
            return Task.CompletedTask;

        if (_folderTreeLoadTask is { IsCompleted: false })
            return _folderTreeLoadTask;

        var sessionVersion = _sessionLoadVersion;
        var sessionId = _sessionId;
        _folderTreeLoadTask = EnsureFolderTreeLoadedCoreAsync(
            sessionVersion,
            sessionId,
            _sessionLoadCts?.Token ?? CancellationToken.None);
        return _folderTreeLoadTask;
    }

    private async Task EnsureFolderTreeLoadedCoreAsync(
        long sessionVersion,
        long sessionId,
        CancellationToken ct)
    {
        BeginTreeLoad(sessionVersion, sessionId);
        FolderTreeStatusText = "Loading top-level folders…";

        try
        {
            var roots = await _repo.GetFolderTreeRootsAsync(sessionId, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentSession(sessionVersion, sessionId, ct))
                return;

            var nodes = new List<FolderTreeNode>(roots.Count);
            foreach (var root in roots)
                nodes.Add(new FolderTreeNode(root, Math.Max(0, root.SubFolderCount)));

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
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionVersion, sessionId))
                FolderTreeStatusText = $"Could not load folder tree: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            EndTreeLoad(sessionVersion, sessionId);
            if (IsCurrentSession(sessionVersion, sessionId))
                _folderTreeLoadTask = null;
        }
    }

    public async Task LoadFolderChildrenAsync(FolderTreeNode node)
    {
        if (node.AreChildrenLoaded || node.IsLoadingChildren || _sessionId <= 0)
            return;

        var sessionVersion = _sessionLoadVersion;
        var sessionId = _sessionId;
        var ct = _sessionLoadCts?.Token ?? CancellationToken.None;

        BeginTreeLoad(sessionVersion, sessionId);
        node.IsLoadingChildren = true;
        FolderTreeStatusText = $"Loading children of {node.DisplayName}…";
        try
        {
            var children = await _repo.GetFolderChildrenAsync(sessionId, node.Folder.FullPath, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentSession(sessionVersion, sessionId, ct))
                return;

            var nodes = new List<FolderTreeNode>(children.Count);
            foreach (var child in children)
                nodes.Add(new FolderTreeNode(child, Math.Max(0, child.SubFolderCount)));

            node.SetChildren(nodes);
            FolderTreeStatusText = children.Count == 0
                ? $"{node.DisplayName} has no nested folders."
                : $"Loaded {children.Count:N0} child folder(s) for {node.DisplayName}.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentSession(sessionVersion, sessionId))
                FolderTreeStatusText = $"Could not load children of {node.DisplayName}: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            if (IsCurrentSession(sessionVersion, sessionId))
                node.IsLoadingChildren = false;
            EndTreeLoad(sessionVersion, sessionId);
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        CancelAndDispose(ref _filterDebounce);
        _filterDebounce = CreateSessionLinkedTokenSource();
        _ = DebounceFilterAsync(_filterDebounce.Token);
    }

    private async Task DebounceFilterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            await ApplyFilterAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                FilterCountLabel = $"Could not filter scan results: {ex.Message}";
            Debug.WriteLine(ex);
        }
    }

    partial void OnSelectedCategoryFilterChanged(string value) =>
        OnPropertyChanged(nameof(HasCategoryFilter));

    [RelayCommand]
    private async Task ClearFilterAsync()
    {
        CancelAndDispose(ref _filterDebounce);
        FilterText = string.Empty;
        SelectedCategoryFilter = string.Empty;
        CancelAndDispose(ref _filterDebounce);
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
    private void OpenWorkspace()
    {
        if (_sessionId > 0)
            _nav.NavigateTo(typeof(ScanWorkspacePage), _sessionId);
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
    private async Task SortFilesByAsync(string column)
    {
        if (_fileSortColumn == column)
            _fileSortDesc = !_fileSortDesc;
        else
        {
            _fileSortColumn = column;
            _fileSortDesc = true;
        }

        RefreshFileSortHeaders();
        await ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task SortFoldersByAsync(string column)
    {
        if (_folderSortColumn == column)
            _folderSortDesc = !_folderSortDesc;
        else
        {
            _folderSortColumn = column;
            _folderSortDesc = true;
        }

        RefreshFolderSortHeaders();
        await ApplyFilterAsync();
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
        {
            await _dialogs.ShowErrorAsync(
                "Could not delete file",
                $"\"{file.FileName}\" was not moved to the Recycle Bin.\n\n{outcome.Error ?? "The file may be locked or access was denied."}");
            return;
        }

        LargestFiles.Remove(file);
        TotalFiles = Math.Max(0, TotalFiles - 1);
        _loadedFileCount = Math.Max(0, _loadedFileCount - 1);
        UpdateFilterCountLabel();

        if (!string.IsNullOrWhiteSpace(outcome.Error))
        {
            await _dialogs.ShowErrorAsync(
                "File moved with a bookkeeping warning",
                outcome.Error);
        }
    }

    private async Task ApplyFilterAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionId <= 0)
            return;

        try
        {
            await LoadPrimaryListsAsync(reset: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FilterCountLabel = $"Could not load scan results: {ex.Message}";
            Debug.WriteLine(ex);
        }
    }

    [RelayCommand]
    private async Task FilterByCategoryAsync(CategoryRow row)
    {
        SelectedCategoryFilter = row.Category;
        await ApplyFilterAsync();
        CategoryFilterApplied?.Invoke(this, EventArgs.Empty);
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

        try
        {
            await ReloadFilesAsync(reset: false, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FilterCountLabel = $"Could not load more files: {ex.Message}";
            Debug.WriteLine(ex);
        }
    }

    [RelayCommand]
    private async Task LoadMoreFoldersAsync()
    {
        if (!CanLoadMoreFolders || _sessionId <= 0)
            return;

        try
        {
            await ReloadFoldersAsync(reset: false, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            FilterCountLabel = $"Could not load more folders: {ex.Message}";
            Debug.WriteLine(ex);
        }
    }

    [RelayCommand]
    private async Task LoadMoreErrorsAsync()
    {
        if (!CanLoadMoreErrors || _sessionId <= 0)
            return;

        var sessionVersion = _sessionLoadVersion;
        var sessionId = _sessionId;
        var loadVersion = ++_errorLoadVersion;
        CancelAndDispose(ref _errorLoadCts);
        _errorLoadCts = CreateSessionLinkedTokenSource();
        var ct = _errorLoadCts.Token;

        IsErrorsLoading = true;
        try
        {
            var page = await _errorRepo.GetErrorsPageForSessionAsync(
                sessionId,
                _loadedErrorCount,
                ErrorPageSize,
                ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentErrorLoad(sessionVersion, sessionId, loadVersion, ct))
                return;

            foreach (var error in page)
                ScanErrors.Add(error);
            _loadedErrorCount += page.Count;
            CanLoadMoreErrors = _loadedErrorCount < ErrorCount;
            ErrorsStatusText = $"Loaded {ScanErrors.Count:N0} of {ErrorCount:N0} scan issue(s).";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentErrorLoad(sessionVersion, sessionId, loadVersion))
                ErrorsStatusText = $"Could not load more scan issues: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            if (IsCurrentErrorLoad(sessionVersion, sessionId, loadVersion))
                IsErrorsLoading = false;
        }
    }

    private void StartSecondaryLoad(long sessionVersion, long sessionId, CancellationToken callerToken)
    {
        CancelAndDispose(ref _categoryLoadCts);
        _categoryLoadCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        IsCategoriesLoading = true;
        _ = RunSecondaryLoadAsync(
            sessionVersion,
            sessionId,
            _categoryLoadCts.Token);
    }

    private async Task RunSecondaryLoadAsync(
        long sessionVersion,
        long sessionId,
        CancellationToken ct)
    {
        try
        {
            var breakdown = await _repo.GetCategoryBreakdownAsync(sessionId, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentSession(sessionVersion, sessionId, ct))
                return;

            CategoryBreakdown.Clear();
            foreach (var (cat, (count, bytes)) in breakdown.OrderByDescending(static x => x.Value.Bytes))
                CategoryBreakdown.Add(new CategoryRow(cat.ToString(), count, ByteSizeConverter.Format(bytes)));

            OnPropertyChanged(nameof(HasCategoryBreakdown));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            if (IsCurrentSession(sessionVersion, sessionId))
                IsCategoriesLoading = false;
        }
    }

    private async Task LoadPrimaryListsAsync(bool reset, CancellationToken ct)
    {
        var operation = BeginPrimaryLoad(ct);
        var filter = FilterText.Trim();
        var category = SelectedCategoryFilter;
        var fileSortColumn = _fileSortColumn;
        var fileSortDesc = _fileSortDesc;
        var folderSortColumn = _folderSortColumn;
        var folderSortDesc = _folderSortDesc;
        var fileOffset = reset ? 0 : _loadedFileCount;
        var folderOffset = reset ? 0 : _loadedFolderCount;

        IsFilesLoading = true;
        IsFoldersLoading = true;
        try
        {
            var filePageTask = _repo.SearchFilesAsync(
                operation.SessionId,
                filter,
                category,
                fileSortColumn,
                fileSortDesc,
                fileOffset,
                FilePageSize,
                operation.Token);
            var fileCountTask = _repo.CountFilesAsync(
                operation.SessionId,
                filter,
                category,
                operation.Token);
            var folderPageTask = _repo.SearchFoldersAsync(
                operation.SessionId,
                filter,
                folderSortColumn,
                folderSortDesc,
                folderOffset,
                FolderPageSize,
                operation.Token);
            var folderCountTask = _repo.CountFoldersAsync(
                operation.SessionId,
                filter,
                operation.Token);

            await Task.WhenAll(filePageTask, fileCountTask, folderPageTask, folderCountTask);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentPrimaryLoad(operation))
                return;

            var filePage = await filePageTask;
            var folderPage = await folderPageTask;
            if (reset)
            {
                LargestFiles.Clear();
                LargestFolders.Clear();
                _loadedFileCount = 0;
                _loadedFolderCount = 0;
            }

            foreach (var file in filePage)
                LargestFiles.Add(file);
            foreach (var folder in folderPage)
                LargestFolders.Add(folder);

            _loadedFileCount += filePage.Count;
            _loadedFolderCount += folderPage.Count;
            TotalFiles = await fileCountTask;
            TotalFolderMatches = await folderCountTask;
            CanLoadMoreFiles = _loadedFileCount < TotalFiles;
            CanLoadMoreFolders = _loadedFolderCount < TotalFolderMatches;
            UpdateFilterCountLabel();
        }
        finally
        {
            if (IsCurrentPrimaryLoad(operation))
            {
                IsFilesLoading = false;
                IsFoldersLoading = false;
            }
        }
    }

    private async Task ReloadFilesAsync(bool reset, CancellationToken ct)
    {
        if (_sessionId <= 0)
            return;

        var operation = BeginPrimaryLoad(ct);
        var filter = FilterText.Trim();
        var category = SelectedCategoryFilter;
        var sortColumn = _fileSortColumn;
        var sortDescending = _fileSortDesc;
        var offset = reset ? 0 : _loadedFileCount;

        IsFilesLoading = true;
        try
        {
            var pageTask = _repo.SearchFilesAsync(
                operation.SessionId,
                filter,
                category,
                sortColumn,
                sortDescending,
                offset,
                FilePageSize,
                operation.Token);
            var countTask = _repo.CountFilesAsync(
                operation.SessionId,
                filter,
                category,
                operation.Token);
            await Task.WhenAll(pageTask, countTask);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentPrimaryLoad(operation))
                return;

            var page = await pageTask;

            if (reset)
            {
                LargestFiles.Clear();
                _loadedFileCount = 0;
            }

            foreach (var file in page)
                LargestFiles.Add(file);

            _loadedFileCount += page.Count;
            TotalFiles = await countTask;
            CanLoadMoreFiles = _loadedFileCount < TotalFiles;
            UpdateFilterCountLabel();
        }
        finally
        {
            if (IsCurrentPrimaryLoad(operation))
                IsFilesLoading = false;
        }
    }

    private async Task ReloadFoldersAsync(bool reset, CancellationToken ct)
    {
        if (_sessionId <= 0)
            return;

        var operation = BeginPrimaryLoad(ct);
        var filter = FilterText.Trim();
        var sortColumn = _folderSortColumn;
        var sortDescending = _folderSortDesc;
        var offset = reset ? 0 : _loadedFolderCount;

        IsFoldersLoading = true;
        try
        {
            var pageTask = _repo.SearchFoldersAsync(
                operation.SessionId,
                filter,
                sortColumn,
                sortDescending,
                offset,
                FolderPageSize,
                operation.Token);
            var countTask = _repo.CountFoldersAsync(operation.SessionId, filter, operation.Token);
            await Task.WhenAll(pageTask, countTask);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentPrimaryLoad(operation))
                return;

            var page = await pageTask;

            if (reset)
            {
                LargestFolders.Clear();
                _loadedFolderCount = 0;
            }

            foreach (var folder in page)
                LargestFolders.Add(folder);

            _loadedFolderCount += page.Count;
            TotalFolderMatches = await countTask;
            CanLoadMoreFolders = _loadedFolderCount < TotalFolderMatches;
        }
        finally
        {
            if (IsCurrentPrimaryLoad(operation))
                IsFoldersLoading = false;
        }
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
        ResetForSessionSwitch(0);
        ScanRoot = string.Empty;
        ScanDate = string.Empty;
        TotalSize = "—";
        TotalFiles = 0;
        SessionNote = string.Empty;
        FilterCountLabel = "No completed scan sessions yet.";
        HasSession = false;
    }

    private PrimaryLoadOperation BeginPrimaryLoad(CancellationToken callerToken)
    {
        CancelAndDispose(ref _primaryLoadCts);
        var sessionToken = _sessionLoadCts?.Token ?? CancellationToken.None;
        _primaryLoadCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, callerToken);
        return new PrimaryLoadOperation(
            _sessionLoadVersion,
            ++_primaryLoadVersion,
            _sessionId,
            _primaryLoadCts.Token);
    }

    private CancellationTokenSource CreateSessionLinkedTokenSource() =>
        CancellationTokenSource.CreateLinkedTokenSource(
            _sessionLoadCts?.Token ?? CancellationToken.None);

    private bool IsCurrentSession(long sessionVersion, long sessionId) =>
        sessionVersion == _sessionLoadVersion && sessionId == _sessionId;

    private bool IsCurrentSession(
        long sessionVersion,
        long sessionId,
        CancellationToken ct) =>
        !ct.IsCancellationRequested && IsCurrentSession(sessionVersion, sessionId);

    private bool IsCurrentPrimaryLoad(PrimaryLoadOperation operation) =>
        operation.LoadVersion == _primaryLoadVersion &&
        IsCurrentSession(operation.SessionVersion, operation.SessionId, operation.Token);

    private bool IsCurrentErrorLoad(
        long sessionVersion,
        long sessionId,
        long loadVersion) =>
        loadVersion == _errorLoadVersion && IsCurrentSession(sessionVersion, sessionId);

    private bool IsCurrentErrorLoad(
        long sessionVersion,
        long sessionId,
        long loadVersion,
        CancellationToken ct) =>
        !ct.IsCancellationRequested && IsCurrentErrorLoad(sessionVersion, sessionId, loadVersion);

    private void BeginTreeLoad(long sessionVersion, long sessionId)
    {
        if (!IsCurrentSession(sessionVersion, sessionId))
            return;

        _activeTreeLoads++;
        IsFolderTreeLoading = true;
    }

    private void EndTreeLoad(long sessionVersion, long sessionId)
    {
        if (!IsCurrentSession(sessionVersion, sessionId))
            return;

        _activeTreeLoads = Math.Max(0, _activeTreeLoads - 1);
        IsFolderTreeLoading = _activeTreeLoads > 0;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
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
