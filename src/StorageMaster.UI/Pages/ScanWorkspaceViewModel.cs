using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class ScanWorkspaceViewModel : ObservableObject
{
    private readonly IScanRepository _scanRepository;
    private readonly IScanErrorRepository _scanErrorRepository;
    private readonly IDuplicateRepository _duplicateRepository;
    private readonly INavigationService _navigation;

    [ObservableProperty] private ScanSession? _session;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _workspaceLoadSucceeded;
    [ObservableProperty] private string _statusText = "Select a completed scan.";
    [ObservableProperty] private string _sessionTitle = "Scan workspace";
    [ObservableProperty] private string _summaryText = "No scan selected.";
    [ObservableProperty] private string _totalSize = "0 B";
    [ObservableProperty] private string _fileCount = "0";
    [ObservableProperty] private string _folderCount = "0";
    [ObservableProperty] private string _errorCount = "0";
    [ObservableProperty] private string _duplicateSummary = "No duplicate run yet.";
    [ObservableProperty] private string _reclaimableSummary = "Run cleanup or duplicate review to estimate savings.";

    public ObservableCollection<FileEntry> LargestFiles { get; } = [];
    public ObservableCollection<FolderEntry> LargestFolders { get; } = [];
    public ObservableCollection<ScanError> Errors { get; } = [];
    public ObservableCollection<CategoryBreakdownItem> CategoryBreakdown { get; } = [];
    public ObservableCollection<DuplicateRun> DuplicateRuns { get; } = [];

    public bool HasSession => Session is not null;
    public bool CanUseSessionActions =>
        WorkspaceLoadSucceeded && !IsLoading && Session?.Status == ScanStatus.Completed;
    public bool HasErrors => Errors.Count > 0;
    public bool HasCategories => CategoryBreakdown.Count > 0;

    public ScanWorkspaceViewModel(
        IScanRepository scanRepository,
        IScanErrorRepository scanErrorRepository,
        IDuplicateRepository duplicateRepository,
        INavigationService navigation)
    {
        _scanRepository = scanRepository;
        _scanErrorRepository = scanErrorRepository;
        _duplicateRepository = duplicateRepository;
        _navigation = navigation;
    }

    partial void OnSessionChanged(ScanSession? value)
    {
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(CanUseSessionActions));
    }

    partial void OnIsLoadingChanged(bool value) =>
        OnPropertyChanged(nameof(CanUseSessionActions));

    partial void OnWorkspaceLoadSucceededChanged(bool value) =>
        OnPropertyChanged(nameof(CanUseSessionActions));

    public async Task LoadAsync(long? sessionId, CancellationToken ct = default)
    {
        WorkspaceLoadSucceeded = false;
        IsLoading = true;
        Session = null;
        SessionTitle = "Scan workspace";
        SummaryText = "Loading scan session...";
        StatusText = "Loading persisted scan data...";
        TotalSize = "—";
        FileCount = "—";
        FolderCount = "—";
        ClearWorkspaceData();
        ErrorCount = "—";
        await Task.Yield();
        try
        {
            var target = sessionId is > 0
                ? await _scanRepository.GetSessionAsync(sessionId.Value, ct)
                : (await _scanRepository.GetRecentSessionsAsync(int.MaxValue, ct))
                    .FirstOrDefault(static session => session.Status == ScanStatus.Completed);

            if (target is null)
            {
                Reset(sessionId is > 0
                    ? "The requested scan session is not available."
                    : "No completed scans are available.");
                return;
            }

            Session = target;
            SessionTitle = $"Scan: {target.RootPath}";
            SummaryText = $"{target.Status} · started {target.StartedUtc.ToLocalTime():g}";
            TotalSize = ByteSizeConverter.Format(target.TotalSizeBytes);
            FileCount = target.TotalFiles.ToString("N0");
            FolderCount = target.TotalFolders.ToString("N0");

            if (target.Status != ScanStatus.Completed)
            {
                TotalSize = "—";
                FileCount = "—";
                FolderCount = "—";
                ErrorCount = "—";
                var failureDetail = string.IsNullOrWhiteSpace(target.ErrorMessage)
                    ? string.Empty
                    : $" Scanner report: {target.ErrorMessage}";
                StatusText =
                    $"This scan is {target.Status}. Partial scan data is hidden; complete a new scan before opening results, cleanup, duplicates, or Space Map.{failureDetail}";
                return;
            }

            await LoadOverviewAsync(target.Id, ct);
            await LoadFilesAsync(target.Id, ct);
            await LoadFoldersAsync(target.Id, ct);
            await LoadErrorsAsync(target.Id, ct);
            await LoadDuplicatesAsync(target.Id, ct);

            ct.ThrowIfCancellationRequested();
            WorkspaceLoadSucceeded = true;
            StatusText = "Workspace data loaded from a completed persisted scan.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenResults()
    {
        if (CanUseSessionActions && Session is not null)
            _navigation.NavigateTo(typeof(ResultsPage), Session.Id);
    }

    [RelayCommand]
    private void OpenSpaceMap()
    {
        if (CanUseSessionActions && Session is not null)
            _navigation.NavigateTo(typeof(SpaceMapPage), Session.Id);
    }

    [RelayCommand]
    private void OpenDuplicates()
    {
        if (CanUseSessionActions && Session is not null)
            _navigation.NavigateTo(typeof(DuplicatesPage), Session.Id);
    }

    [RelayCommand]
    private void OpenCleanup()
    {
        if (CanUseSessionActions && Session is not null)
            _navigation.NavigateTo(typeof(CleanupPage), Session.Id);
    }

    private async Task LoadOverviewAsync(long sessionId, CancellationToken ct)
    {
        CategoryBreakdown.Clear();
        var breakdown = await _scanRepository.GetCategoryBreakdownAsync(sessionId, ct);
        foreach (var item in breakdown.OrderByDescending(static item => item.Value.Bytes))
        {
            CategoryBreakdown.Add(new CategoryBreakdownItem(
                item.Key.ToString(),
                item.Value.Count,
                item.Value.Bytes,
                ByteSizeConverter.Format(item.Value.Bytes)));
        }

        OnPropertyChanged(nameof(HasCategories));
    }

    private async Task LoadFilesAsync(long sessionId, CancellationToken ct)
    {
        Replace(LargestFiles, await _scanRepository.SearchFilesAsync(sessionId, null, null, "Size", true, 0, 50, ct));
    }

    private async Task LoadFoldersAsync(long sessionId, CancellationToken ct)
    {
        Replace(LargestFolders, await _scanRepository.SearchFoldersAsync(sessionId, null, "Size", true, 0, 50, ct));
    }

    private async Task LoadErrorsAsync(long sessionId, CancellationToken ct)
    {
        var count = await _scanErrorRepository.CountErrorsForSessionAsync(sessionId, ct);
        ErrorCount = count.ToString("N0");
        Replace(Errors, await _scanErrorRepository.GetErrorsPageForSessionAsync(sessionId, 0, 50, ct));
        OnPropertyChanged(nameof(HasErrors));
    }

    private async Task LoadDuplicatesAsync(long sessionId, CancellationToken ct)
    {
        Replace(DuplicateRuns, await _duplicateRepository.GetRunsForSessionAsync(sessionId, ct));
        var latest = DuplicateRuns.OrderByDescending(static run => run.StartedUtc).FirstOrDefault();
        if (latest is null)
        {
            DuplicateSummary = "No duplicate run yet.";
            ReclaimableSummary = "Run duplicate review to estimate recoverable space.";
            return;
        }

        DuplicateSummary = $"{latest.GroupCount:N0} groups · {latest.Status}";
        ReclaimableSummary = $"{ByteSizeConverter.Format(latest.ReclaimableBytes)} reclaimable from latest duplicate run.";
    }

    private void ClearWorkspaceData()
    {
        LargestFiles.Clear();
        LargestFolders.Clear();
        Errors.Clear();
        CategoryBreakdown.Clear();
        DuplicateRuns.Clear();
        ErrorCount = "0";
        DuplicateSummary = "No duplicate run yet.";
        ReclaimableSummary = "Run cleanup or duplicate review to estimate savings.";
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasCategories));
    }

    private void Reset(string message)
    {
        WorkspaceLoadSucceeded = false;
        Session = null;
        SessionTitle = "Scan workspace";
        SummaryText = message;
        StatusText = message;
        TotalSize = "0 B";
        FileCount = "0";
        FolderCount = "0";
        ClearWorkspaceData();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}

public sealed record CategoryBreakdownItem(
    string Category,
    long Count,
    long Bytes,
    string SizeDisplay);
