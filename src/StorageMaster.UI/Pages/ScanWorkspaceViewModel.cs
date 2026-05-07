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

    public async Task LoadAsync(long? sessionId, CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var target = sessionId is > 0
                ? await _scanRepository.GetSessionAsync(sessionId.Value, ct)
                : (await _scanRepository.GetRecentSessionsAsync(1, ct)).FirstOrDefault();

            if (target is null)
            {
                Reset("No completed scans are available.");
                return;
            }

            Session = target;
            SessionTitle = $"Scan: {target.RootPath}";
            SummaryText = $"{target.Status} · started {target.StartedUtc.ToLocalTime():g}";
            TotalSize = ByteSizeConverter.Format(target.TotalSizeBytes);
            FileCount = target.TotalFiles.ToString("N0");
            FolderCount = target.TotalFolders.ToString("N0");
            StatusText = "Workspace data loaded from persisted scan results.";

            await LoadOverviewAsync(target.Id, ct);
            await LoadFilesAsync(target.Id, ct);
            await LoadFoldersAsync(target.Id, ct);
            await LoadErrorsAsync(target.Id, ct);
            await LoadDuplicatesAsync(target.Id, ct);

            OnPropertyChanged(nameof(HasSession));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenResults()
    {
        if (Session is not null)
            _navigation.NavigateTo(typeof(ResultsPage), Session.Id);
    }

    [RelayCommand]
    private void OpenSpaceMap()
    {
        if (Session is not null)
            _navigation.NavigateTo(typeof(SpaceMapPage), Session.Id);
    }

    [RelayCommand]
    private void OpenDuplicates()
    {
        if (Session is not null)
            _navigation.NavigateTo(typeof(DuplicatesPage), Session.Id);
    }

    [RelayCommand]
    private void OpenCleanup()
    {
        if (Session is not null)
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

    private void Reset(string message)
    {
        Session = null;
        SessionTitle = "Scan workspace";
        SummaryText = message;
        StatusText = message;
        LargestFiles.Clear();
        LargestFolders.Clear();
        Errors.Clear();
        CategoryBreakdown.Clear();
        DuplicateRuns.Clear();
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasCategories));
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
