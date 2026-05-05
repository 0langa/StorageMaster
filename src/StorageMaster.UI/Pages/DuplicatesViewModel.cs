using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public enum DuplicateScopeMode
{
    WholeSession,
    IncludedFolders,
    ExcludedFolders,
}

public sealed record DuplicateCategoryOption(
    string Label,
    IReadOnlyList<FileTypeCategory> Categories,
    bool UsesCustomExtensions = false);

public sealed partial class DuplicateMemberItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isKeeper;

    public DuplicateGroupMember Member { get; }
    public string SizeText => ByteSizeConverter.Format(Member.SizeBytes);
    public string ModifiedText => Member.ModifiedUtc.ToString("g");
    public string StatusText => IsKeeper ? "Keeper" : IsSelected ? "Selected" : "Review";

    public DuplicateMemberItem(DuplicateGroupMember member)
    {
        Member = member;
        _isSelected = member.IsSelected;
        _isKeeper = member.IsKeeper;
    }

    partial void OnIsKeeperChanged(bool value)
    {
        if (value && IsSelected)
            IsSelected = false;
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(StatusText));
}

public sealed partial class DuplicateGroupItem : ObservableObject
{
    public DuplicateGroup Group { get; }
    public ObservableCollection<DuplicateMemberItem> Members { get; } = [];

    public string MethodText => Group.Method switch
    {
        DuplicateMethod.ExactSha256 => "Exact match",
        DuplicateMethod.NormalizedText => "Normalized-equivalent",
        DuplicateMethod.ImagePHash => "Perceptual image match",
        DuplicateMethod.VideoPHash => "Perceptual video match",
        _ => Group.Method.ToString(),
    };

    public string BadgeText => CanAutoSelect ? "Auto-selected" : "Review required";
    public string ConfidenceText => $"{Group.Confidence:P0}";
    public string TotalSizeText => ByteSizeConverter.Format(Group.TotalBytes);
    public string ReclaimableText => ByteSizeConverter.Format(Group.ReclaimableBytes);
    public int SelectedCount => Members.Count(static member => member.IsSelected && !member.IsKeeper);
    public int MemberCount => Members.Count;
    public bool CanAutoSelect => Group.Method == DuplicateMethod.ExactSha256;
    public bool RequiresReview => !CanAutoSelect;
    public string PathSummary => string.Join(" | ", Members.Take(2).Select(static m => m.Member.FullPath));

    public DuplicateGroupItem(DuplicateGroup group, IEnumerable<DuplicateGroupMember> members)
    {
        Group = group;
        foreach (var member in members)
        {
            var item = new DuplicateMemberItem(member);
            item.PropertyChanged += OnMemberPropertyChanged;
            Members.Add(item);
        }
    }

    public void ApplyKeeperPolicy(KeeperPolicy policy)
    {
        var ordered = policy switch
        {
            KeeperPolicy.Oldest => Members.OrderBy(static member => member.Member.ModifiedUtc).ThenBy(static member => member.Member.FullPath.Length),
            KeeperPolicy.ShortestPath => Members.OrderBy(static member => member.Member.FullPath.Length).ThenByDescending(static member => member.Member.ModifiedUtc),
            KeeperPolicy.LongestPath => Members.OrderByDescending(static member => member.Member.FullPath.Length).ThenByDescending(static member => member.Member.ModifiedUtc),
            _ => Members.OrderByDescending(static member => member.Member.ModifiedUtc).ThenBy(static member => member.Member.FullPath.Length),
        };

        var keeper = ordered.FirstOrDefault();
        if (keeper is null)
            return;

        var priorSelections = Members.ToDictionary(static member => member.Member.FileEntryId, static member => member.IsSelected);

        foreach (var member in Members)
        {
            member.IsKeeper = ReferenceEquals(member, keeper);
            member.IsSelected = member.IsKeeper
                ? false
                : member.Member.ExistsNow && (CanAutoSelect || priorSelections.GetValueOrDefault(member.Member.FileEntryId));
        }

        NotifySummaryChanged();
    }

    private void OnMemberPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DuplicateMemberItem.IsSelected) or nameof(DuplicateMemberItem.IsKeeper))
            NotifySummaryChanged();
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(BadgeText));
    }
}

public sealed partial class DuplicatesViewModel : ObservableObject
{
    private const int GroupPageSize = 100;
    private const int ErrorPageSize = 100;

    private readonly IScanRepository _scanRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IDuplicateFinderService _duplicateFinderService;
    private readonly IDuplicateRepository _duplicateRepository;
    private readonly IDuplicateDeletionService _duplicateDeletionService;
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyDictionary<DuplicateMethod, IDuplicateDetectionStrategy> _strategyMap;
    private readonly List<DuplicateGroupItem> _allGroups = [];
    private DuplicateRunSummary _runSummary = new();
    private int _currentGroupPage = 1;
    private bool _hasMoreGroupPages;
    private int _currentErrorPage = 1;
    private bool _hasMoreErrorPages;

    private CancellationTokenSource? _analysisCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private string _statusText = "Choose scan session, run duplicate analysis, review groups, delete copies.";
    [ObservableProperty] private string _progressPhase = string.Empty;
    [ObservableProperty] private int _progressProcessed;
    [ObservableProperty] private int _progressTotal;
    [ObservableProperty] private int _minimumSizeMb = 1;
    [ObservableProperty] private bool _useRecycleBin = true;
    [ObservableProperty] private bool _useQuarantine;
    [ObservableProperty] private bool _includeNormalizedText;
    [ObservableProperty] private bool _includeImagePHash;
    [ObservableProperty] private bool _includeVideoPHash;
    [ObservableProperty] private bool _includeHiddenFiles;
    [ObservableProperty] private bool _includeReparsePoints;
    [ObservableProperty] private KeeperPolicy _keeperPolicy = KeeperPolicy.Newest;
    [ObservableProperty] private ScanSession? _selectedSession;
    [ObservableProperty] private DuplicateRun? _latestRun;
    [ObservableProperty] private string _customExtensionsText = string.Empty;
    [ObservableProperty] private string _scopePathsText = string.Empty;
    [ObservableProperty] private string _excludedScopePathsText = string.Empty;
    [ObservableProperty] private DuplicateScopeMode _selectedScopeMode = DuplicateScopeMode.WholeSession;
    [ObservableProperty] private DuplicateCategoryOption? _selectedCategory;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DuplicateGroupItem? _selectedGroup;
    [ObservableProperty] private DuplicateGroupSortBy _groupSortBy = DuplicateGroupSortBy.ReclaimableBytesDesc;
    [ObservableProperty] private bool _filterSelectedOnly;
    [ObservableProperty] private bool _filterMissingOnly;
    [ObservableProperty] private bool _filterErroredOnly;
    [ObservableProperty] private double _minimumConfidenceFilter;

    public ObservableCollection<ScanSession> Sessions { get; } = [];
    public ObservableCollection<DuplicateGroupItem> Groups { get; } = [];
    public ObservableCollection<DuplicateError> Errors { get; } = [];

    public Array KeeperPolicyOptions => Enum.GetValues(typeof(KeeperPolicy));
    public Array ScopeModeOptions => Enum.GetValues(typeof(DuplicateScopeMode));
    public Array GroupSortOptions => Enum.GetValues(typeof(DuplicateGroupSortBy));
    public ObservableCollection<DuplicateCategoryOption> CategoryOptions { get; } =
    [
        new("All files", []),
        new("Images", [FileTypeCategory.Image]),
        new("Videos", [FileTypeCategory.Video]),
        new("Audio", [FileTypeCategory.Audio]),
        new("Documents / text", [FileTypeCategory.Document, FileTypeCategory.SourceCode, FileTypeCategory.Log]),
        new("Archives", [FileTypeCategory.Archive]),
        new("Custom extensions", [], true),
    ];

    public bool HasGroups => Groups.Count > 0;
    public bool HasErrors => Errors.Count > 0;
    public bool HasLatestRun => LatestRun is not null;
    public bool HasSelectedGroup => SelectedGroup is not null;
    public bool CanRun => SelectedSession is not null && !IsRunning && !IsDeleting;
    public bool CanCancel => IsRunning;
    public bool CanDeleteSelected => _allGroups.Any(static group => group.SelectedCount > 0) && !IsDeleting && !IsRunning;
    public bool CanLoadPreviousGroups => _currentGroupPage > 1 && !IsLoading && !IsRunning;
    public bool CanLoadNextGroups => _hasMoreGroupPages && !IsLoading && !IsRunning;
    public bool CanLoadMoreErrors => _hasMoreErrorPages && !IsLoading;
    public bool IsImagePHashAvailable => IsMethodAvailable(DuplicateMethod.ImagePHash);
    public bool IsVideoPHashAvailable => IsMethodAvailable(DuplicateMethod.VideoPHash);
    public string ProgressText => ProgressTotal <= 0 ? string.Empty : $"{ProgressProcessed:N0} / {ProgressTotal:N0}";
    public int CurrentPage => _currentGroupPage;
    public int TotalGroupCount => (int)_runSummary.GroupCount;
    public int ExactGroupCount => (int)_runSummary.ExactGroupCount;
    public int ReviewGroupCount => (int)_runSummary.ReviewGroupCount;
    public string ReclaimableText => ByteSizeConverter.Format(_runSummary.ReclaimableBytes);
    public string ErrorSummary => _runSummary.ErrorCount == 0 ? "No errors or skipped files." : $"{_runSummary.ErrorCount:N0} error / skipped item(s)";
    public string LatestRunSummary => LatestRun is null
        ? "No duplicate analysis has been saved for this scan session yet."
        : $"Last run: {LatestRun.Status} on {LatestRun.CompletedUtc?.ToString("g") ?? LatestRun.StartedUtc.ToString("g")}. " +
          $"{LatestRun.GroupCount:N0} group(s), {ByteSizeConverter.Format(LatestRun.ReclaimableBytes)} reclaimable, {LatestRun.ErrorCount:N0} error(s).";

    public string ProviderSummary
    {
        get
        {
            var parts = _strategyMap.Values
                .OrderBy(static strategy => strategy.Method)
                .Select(strategy => strategy.IsAvailable
                    ? strategy.DisplayName
                    : $"{strategy.DisplayName} unavailable ({strategy.UnavailableReason ?? "not ready"})");
            return "Methods: " + string.Join(", ", parts) + ".";
        }
    }

    public DuplicatesViewModel(
        IScanRepository scanRepository,
        ISettingsRepository settingsRepository,
        IDuplicateFinderService duplicateFinderService,
        IDuplicateRepository duplicateRepository,
        IDuplicateDeletionService duplicateDeletionService,
        IDialogService dialogs,
        IEnumerable<IDuplicateDetectionStrategy> strategies)
    {
        _scanRepository = scanRepository;
        _settingsRepository = settingsRepository;
        _duplicateFinderService = duplicateFinderService;
        _duplicateRepository = duplicateRepository;
        _duplicateDeletionService = duplicateDeletionService;
        _dialogs = dialogs;
        _strategyMap = strategies.ToDictionary(static s => s.Method);
        SelectedCategory = CategoryOptions.FirstOrDefault();
    }

    partial void OnSelectedSessionChanged(ScanSession? value)
    {
        OnPropertyChanged(nameof(CanRun));
        _ = LoadLatestRunAsync();
    }

    partial void OnKeeperPolicyChanged(KeeperPolicy value)
    {
        foreach (var group in _allGroups)
            group.ApplyKeeperPolicy(value);

        RaiseSummaryProperties();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    partial void OnIsDeletingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    partial void OnSearchTextChanged(string value) => _ = ReloadCurrentGroupPageAsync();
    partial void OnGroupSortByChanged(DuplicateGroupSortBy value) => _ = ReloadCurrentGroupPageAsync();
    partial void OnFilterSelectedOnlyChanged(bool value) => _ = ReloadCurrentGroupPageAsync();
    partial void OnFilterMissingOnlyChanged(bool value) => _ = ReloadCurrentGroupPageAsync();
    partial void OnFilterErroredOnlyChanged(bool value) => _ = ReloadCurrentGroupPageAsync();
    partial void OnMinimumConfidenceFilterChanged(double value) => _ = ReloadCurrentGroupPageAsync();

    partial void OnSelectedGroupChanged(DuplicateGroupItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedGroup));
    }

    public async Task InitializeAsync(long? preselectedSessionId = null)
    {
        IsLoading = true;
        try
        {
            var settings = await _settingsRepository.LoadAsync();
            MinimumSizeMb = settings.DuplicateMinimumSizeMb;
            KeeperPolicy = settings.DuplicateKeeperPolicy;
            IncludeNormalizedText = settings.DuplicateUseNormalizedText && IsMethodAvailable(DuplicateMethod.NormalizedText);
            IncludeImagePHash = settings.DuplicateUseImagePHash && IsImagePHashAvailable;
            IncludeVideoPHash = settings.DuplicateUseVideoPHash && IsVideoPHashAvailable;
            UseRecycleBin = settings.PreferRecycleBin;
            UseQuarantine = false;
            IncludeHiddenFiles = settings.ShowHiddenFiles;
            IncludeReparsePoints = false;

            Sessions.Clear();
            var sessions = await _scanRepository.GetRecentSessionsAsync(50);
            foreach (var session in sessions.Where(static session => session.Status == ScanStatus.Completed))
                Sessions.Add(session);

            SelectedSession = preselectedSessionId is long selectedId
                ? Sessions.FirstOrDefault(session => session.Id == selectedId) ?? Sessions.FirstOrDefault()
                : Sessions.FirstOrDefault();

            await LoadLatestRunAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RunAnalysisAsync()
    {
        if (SelectedSession is null || SelectedCategory is null)
            return;

        var extensions = ParsePaths(CustomExtensionsText)
            .Select(static e => e.StartsWith('.') ? e : "." + e)
            .Select(static e => e.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (SelectedCategory.UsesCustomExtensions && extensions.Count == 0)
        {
            StatusText = "Enter at least one custom extension for custom-scope dedupe.";
            return;
        }

        var includedPaths = SelectedScopeMode == DuplicateScopeMode.IncludedFolders
            ? ParsePaths(ScopePathsText)
            : [];
        if (SelectedScopeMode == DuplicateScopeMode.IncludedFolders && includedPaths.Count == 0)
        {
            StatusText = "Add one or more folder prefixes for included-folder scope.";
            return;
        }

        _analysisCts = new CancellationTokenSource();
        IsRunning = true;
        ProgressProcessed = 0;
        ProgressTotal = 0;
        ProgressPhase = string.Empty;
        StatusText = "Starting duplicate analysis…";
        Groups.Clear();
        Errors.Clear();
        _allGroups.Clear();
        RaiseSummaryProperties();

        try
        {
            var settings = await _settingsRepository.LoadAsync();

            var methods = new List<DuplicateMethod> { DuplicateMethod.ExactSha256 };
            if (IncludeNormalizedText && IsMethodAvailable(DuplicateMethod.NormalizedText))
                methods.Add(DuplicateMethod.NormalizedText);
            if (IncludeImagePHash && IsImagePHashAvailable)
                methods.Add(DuplicateMethod.ImagePHash);
            if (IncludeVideoPHash && IsVideoPHashAvailable)
                methods.Add(DuplicateMethod.VideoPHash);

            var excludedPaths = settings.ExcludedPaths.ToList();
            if (SelectedScopeMode == DuplicateScopeMode.ExcludedFolders)
                excludedPaths.AddRange(ParsePaths(ExcludedScopePathsText));

            LatestRun = await _duplicateFinderService.RunAsync(
                new DuplicateScanOptions
                {
                    SessionId = SelectedSession.Id,
                    MinimumSizeBytes = Math.Max(0, MinimumSizeMb) * 1024L * 1024L,
                    Methods = methods,
                    KeeperPolicy = KeeperPolicy,
                    MaxConcurrency = Math.Max(1, settings.ScanParallelism),
                    PerDriveConcurrency = Math.Max(1, Math.Min(4, settings.ScanParallelism / 2)),
                    IncludeExtensions = extensions,
                    IncludeCategories = SelectedCategory.Categories,
                    IncludedPaths = includedPaths,
                    ExcludedPaths = excludedPaths,
                    IncludeHiddenFiles = IncludeHiddenFiles,
                    IncludeReparsePoints = IncludeReparsePoints,
                },
                new Progress<DuplicateDetectionProgress>(p =>
                {
                    ProgressPhase = p.Phase;
                    ProgressProcessed = p.Processed;
                    ProgressTotal = Math.Max(p.Total, 1);
                    StatusText = $"{p.Phase}: {p.Processed:N0} / {p.Total:N0} ({p.GroupsFound:N0} groups, {p.Errors:N0} errors)";
                }),
                _analysisCts.Token);

            await LoadLatestRunAsync();
            StatusText = LatestRun?.Status == DuplicateRunStatus.Cancelled
                ? "Analysis cancelled."
                : _runSummary.GroupCount > 0
                    ? $"Loaded page {_currentGroupPage:N0} of {_runSummary.GroupCount:N0} duplicate group(s). Review before deleting."
                    : "No duplicate groups found for current scope.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Duplicate analysis failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _analysisCts?.Dispose();
            _analysisCts = null;
        }
    }

    [RelayCommand]
    private void CancelAnalysis()
    {
        _analysisCts?.Cancel();
        StatusText = "Cancelling…";
    }

    [RelayCommand]
    private async Task PreviousGroupsPageAsync()
    {
        if (_currentGroupPage <= 1 || LatestRun is null)
            return;

        await LoadGroupsPageAsync(_currentGroupPage - 1);
        StatusText = $"Loaded page {_currentGroupPage:N0}.";
    }

    [RelayCommand]
    private async Task NextGroupsPageAsync()
    {
        if (!_hasMoreGroupPages || LatestRun is null)
            return;

        await LoadGroupsPageAsync(_currentGroupPage + 1);
        StatusText = $"Loaded page {_currentGroupPage:N0}.";
    }

    [RelayCommand]
    private async Task LoadMoreErrorsAsync()
    {
        if (!_hasMoreErrorPages || LatestRun is null)
            return;

        await LoadErrorsPageAsync(_currentErrorPage + 1, append: true);
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (LatestRun is null)
            return;

        var exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster",
            "exports");
        Directory.CreateDirectory(exportDir);
        var filePath = Path.Combine(exportDir, $"dedupe-{LatestRun.Id}-report.csv");

        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("GroupId,Method,Confidence,KeeperPath,DuplicatePath,SizeBytes,Selected,ExistsNow");

        var page = 1;
        while (true)
        {
            var groups = await _duplicateRepository.GetDuplicateGroupsPageAsync(
                LatestRun.Id, page, GroupPageSize, null, DuplicateGroupSortBy.ReclaimableBytesDesc);
            if (groups.Count == 0)
                break;

            foreach (var group in groups)
            {
                var members = await _duplicateRepository.GetDuplicateGroupMembersAsync(group.Id);
                var keeper = members.FirstOrDefault(static m => m.IsKeeper)?.FullPath ?? string.Empty;
                foreach (var member in members.Where(static m => !m.IsKeeper))
                {
                    var row = string.Join(',',
                        Csv(group.Id.ToString()),
                        Csv(group.Method.ToString()),
                        Csv(group.Confidence.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)),
                        Csv(keeper),
                        Csv(member.FullPath),
                        Csv(member.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Csv(member.IsSelected ? "1" : "0"),
                        Csv(member.ExistsNow ? "1" : "0"));
                    await writer.WriteLineAsync(row);
                }
            }
            page++;
        }

        StatusText = $"CSV export saved to {filePath}.";
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (LatestRun is null)
            return;

        var exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster",
            "exports");
        Directory.CreateDirectory(exportDir);
        var filePath = Path.Combine(exportDir, $"dedupe-{LatestRun.Id}-report.json");

        var payload = new
        {
            run = LatestRun,
            summary = _runSummary,
            errors = await _duplicateRepository.GetErrorsForRunAsync(LatestRun.Id),
            groups = await _duplicateRepository.GetGroupsForRunAsync(LatestRun.Id),
        };
        await File.WriteAllTextAsync(filePath, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }));

        StatusText = $"JSON export saved to {filePath}.";
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (LatestRun is null)
            return;

        var exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster",
            "exports");
        Directory.CreateDirectory(exportDir);
        var filePath = Path.Combine(exportDir, $"dedupe-{LatestRun.Id}-report.html");

        var groups = await _duplicateRepository.GetGroupsForRunAsync(LatestRun.Id);
        var lines = new List<string>
        {
            "<!doctype html>",
            "<html><head><meta charset=\"utf-8\"><title>StorageMaster Duplicate Report</title>",
            "<style>body{font-family:Segoe UI,Arial,sans-serif;padding:24px;}table{border-collapse:collapse;width:100%;}th,td{border:1px solid #ddd;padding:8px;}th{background:#f3f3f3;text-align:left;}tr:nth-child(even){background:#fafafa}</style>",
            "</head><body>",
            $"<h1>StorageMaster Duplicate Report (Run {LatestRun.Id})</h1>",
            $"<p>Generated {DateTime.Now:G}. Groups: {_runSummary.GroupCount:N0}, Reclaimable: {ByteSizeConverter.Format(_runSummary.ReclaimableBytes)}</p>",
            "<table><thead><tr><th>Group</th><th>Method</th><th>Confidence</th><th>Reclaimable</th><th>Keeper</th><th>Duplicate</th></tr></thead><tbody>",
        };

        foreach (var group in groups)
        {
            var members = await _duplicateRepository.GetDuplicateGroupMembersAsync(group.Id);
            var keeper = members.FirstOrDefault(static m => m.IsKeeper)?.FullPath ?? string.Empty;
            foreach (var member in members.Where(static m => !m.IsKeeper))
            {
                lines.Add("<tr>" +
                          $"<td>{EscapeHtml(group.Id.ToString())}</td>" +
                          $"<td>{EscapeHtml(group.Method.ToString())}</td>" +
                          $"<td>{EscapeHtml($"{group.Confidence:P0}")}</td>" +
                          $"<td>{EscapeHtml(ByteSizeConverter.Format(group.ReclaimableBytes))}</td>" +
                          $"<td>{EscapeHtml(keeper)}</td>" +
                          $"<td>{EscapeHtml(member.FullPath)}</td>" +
                          "</tr>");
            }
        }

        lines.Add("</tbody></table></body></html>");
        await File.WriteAllLinesAsync(filePath, lines);
        StatusText = $"HTML export saved to {filePath}.";
    }

    [RelayCommand]
    private void KeepNewest(DuplicateGroupItem group)
    {
        group.ApplyKeeperPolicy(KeeperPolicy.Newest);
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    [RelayCommand]
    private void KeepOldest(DuplicateGroupItem group)
    {
        group.ApplyKeeperPolicy(KeeperPolicy.Oldest);
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selectedGroups = _allGroups.Where(static group => group.SelectedCount > 0).ToList();
        if (selectedGroups.Count == 0)
            return;

        var selectedMembers = selectedGroups.SelectMany(static group => group.Members).Count(static member => member.IsSelected && !member.IsKeeper);
        var deletionMethod = UseQuarantine ? DeletionMethod.Quarantine
                           : UseRecycleBin ? DeletionMethod.RecycleBin
                           : DeletionMethod.Permanent;
        var methodLabel = deletionMethod switch
        {
            DeletionMethod.Quarantine => "quarantine (restorable)",
            DeletionMethod.RecycleBin => "Recycle Bin",
            _ => "permanent deletion",
        };

        var confirmed = await _dialogs.ConfirmAsync(
            "Delete selected duplicates",
            $"Delete {selectedMembers:N0} duplicate file(s)?\n\nOne keeper per group stays untouched. Method: {methodLabel}.",
            $"Delete via {methodLabel}");
        if (!confirmed)
            return;

        IsDeleting = true;
        try
        {
            long freed = 0;
            foreach (var group in selectedGroups)
            {
                var rawMembers = group.Members.Select(member => member.Member with
                {
                    IsSelected = member.IsSelected,
                    IsKeeper = member.IsKeeper,
                }).ToList();

                freed += await _duplicateDeletionService.DeleteSelectedAsync(
                    group.Group,
                    rawMembers,
                    deletionMethod);
            }

            StatusText = $"Deleted selected duplicates. Reclaimed {ByteSizeConverter.Format(freed)}.";
            await LoadLatestRunAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Duplicate deletion failed: {ex.Message}";
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private async Task LoadLatestRunAsync()
    {
        Groups.Clear();
        Errors.Clear();
        _allGroups.Clear();
        LatestRun = null;
        _runSummary = new DuplicateRunSummary();
        _currentGroupPage = 1;
        _hasMoreGroupPages = false;
        _currentErrorPage = 1;
        _hasMoreErrorPages = false;
        RaiseSummaryProperties();

        if (SelectedSession is null)
        {
            StatusText = "Choose a completed scan session to review duplicates.";
            return;
        }

        var runs = await _duplicateRepository.GetRunsForSessionAsync(SelectedSession.Id);
        LatestRun = runs.FirstOrDefault();
        OnPropertyChanged(nameof(HasLatestRun));
        OnPropertyChanged(nameof(LatestRunSummary));
        if (LatestRun is null)
        {
            StatusText = "No duplicate analysis has been run for this scan yet.";
            return;
        }

        _runSummary = await _duplicateRepository.GetDuplicateRunSummaryAsync(LatestRun.Id);
        await LoadGroupsPageAsync(1);
        await LoadErrorsPageAsync(1, append: false);

        StatusText = _runSummary.GroupCount > 0
            ? $"Loaded page {_currentGroupPage:N0} of duplicate groups ({_runSummary.GroupCount:N0} total)."
            : "Latest duplicate analysis completed without duplicate groups.";
    }

    private async Task ReloadCurrentGroupPageAsync()
    {
        if (LatestRun is null || IsRunning)
            return;

        await LoadGroupsPageAsync(_currentGroupPage);
    }

    private async Task LoadGroupsPageAsync(int page)
    {
        if (LatestRun is null)
            return;

        var filter = new DuplicateGroupQueryFilter
        {
            SearchText = SearchText,
            MinConfidence = MinimumConfidenceFilter > 0 ? MinimumConfidenceFilter : null,
            HasSelectedMembers = FilterSelectedOnly ? true : null,
            ExistsNow = FilterMissingOnly ? false : null,
            IncludeErroredOnly = FilterErroredOnly,
        };

        var groups = (await _duplicateRepository.GetDuplicateGroupsPageAsync(
            LatestRun.Id,
            page,
            GroupPageSize + 1,
            filter,
            GroupSortBy)).ToList();

        if (groups.Count == 0 && page > 1)
        {
            await LoadGroupsPageAsync(1);
            return;
        }

        _currentGroupPage = Math.Max(1, page);
        _hasMoreGroupPages = groups.Count > GroupPageSize;
        if (_hasMoreGroupPages)
            groups = groups.Take(GroupPageSize).ToList();
        Groups.Clear();
        _allGroups.Clear();

        foreach (var group in groups)
        {
            var members = await _duplicateRepository.GetDuplicateGroupMembersAsync(group.Id);
            var item = new DuplicateGroupItem(group, members);
            foreach (var member in item.Members)
                member.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanDeleteSelected));
            item.ApplyKeeperPolicy(KeeperPolicy);
            _allGroups.Add(item);
            Groups.Add(item);
        }

        SelectedGroup = Groups.FirstOrDefault();
        RaiseSummaryProperties();
    }

    private async Task LoadErrorsPageAsync(int page, bool append)
    {
        if (LatestRun is null)
            return;

        var errors = (await _duplicateRepository.GetDuplicateErrorsPageAsync(LatestRun.Id, page, ErrorPageSize + 1)).ToList();
        _currentErrorPage = Math.Max(1, page);
        _hasMoreErrorPages = errors.Count > ErrorPageSize;
        if (_hasMoreErrorPages)
            errors = errors.Take(ErrorPageSize).ToList();

        if (!append)
            Errors.Clear();
        foreach (var error in errors)
            Errors.Add(error);

        RaiseSummaryProperties();
    }
    private bool IsMethodAvailable(DuplicateMethod method) =>
        _strategyMap.TryGetValue(method, out var strategy) && strategy.IsAvailable;

    private static List<string> ParsePaths(string text) =>
        text.Split(['\r', '\n', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void RaiseSummaryProperties()
    {
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasSelectedGroup));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(TotalGroupCount));
        OnPropertyChanged(nameof(ExactGroupCount));
        OnPropertyChanged(nameof(ReviewGroupCount));
        OnPropertyChanged(nameof(ReclaimableText));
        OnPropertyChanged(nameof(ErrorSummary));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(CanLoadPreviousGroups));
        OnPropertyChanged(nameof(CanLoadNextGroups));
        OnPropertyChanged(nameof(CanLoadMoreErrors));
    }

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string EscapeHtml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal);
}
