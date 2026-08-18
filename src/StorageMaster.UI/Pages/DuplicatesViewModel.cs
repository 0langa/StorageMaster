using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
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

public sealed record DuplicateMethodFilterOption(string Label, DuplicateMethod? Method);

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
    public string MemberCountText => $"{MemberCount:N0} file(s)";
    public string ReclaimableSummaryText => $"Can free {ReclaimableText}";
    public string SelectedCountText => $"{SelectedCount:N0} selected";
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
        OnPropertyChanged(nameof(SelectedCountText));
    }
}

public sealed partial class DuplicatesViewModel : ObservableObject
{
    private const int GroupPageSize = 100;
    private const int ErrorPageSize = 100;

    private sealed record GroupPageResult(
        int Page,
        bool HasMore,
        IReadOnlyList<DuplicateGroupItem> Items);

    private sealed record ErrorPageResult(
        int Page,
        bool HasMore,
        IReadOnlyList<DuplicateError> Items);

    private sealed record DuplicateDeletionGroupPlan(
        DuplicateGroup Group,
        IReadOnlyList<DuplicateGroupMember> Members);

    private sealed record DuplicateDeletionPlan(
        int Page,
        DeletionMethod Method,
        int SelectedMemberCount,
        IReadOnlyList<DuplicateDeletionGroupPlan> Groups);

    private readonly IScanRepository _scanRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IDuplicateFinderService _duplicateFinderService;
    private readonly IDuplicateRepository _duplicateRepository;
    private readonly IDuplicateDeletionService _duplicateDeletionService;
    private readonly IDuplicatePreviewService _previewService;
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyDictionary<DuplicateMethod, IDuplicateDetectionStrategy> _strategyMap;
    private readonly List<DuplicateGroupItem> _allGroups = [];
    private DuplicateRunSummary _runSummary = new();
    private int _currentGroupPage = 1;
    private bool _hasMoreGroupPages;
    private int _currentErrorPage = 1;
    private bool _hasMoreErrorPages;

    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _latestRunCts;
    private CancellationTokenSource? _groupReloadCts;
    private CancellationTokenSource? _errorReloadCts;
    private CancellationTokenSource? _previewLoadCts;
    private CancellationTokenSource? _filterDebounceCts;
    private CancellationTokenSource? _exportCts;
    private long _lifetimeVersion;
    private long _latestRunVersion;
    private long _groupReloadVersion;
    private long _errorReloadVersion;
    private long _previewLoadVersion;
    private bool _groupReloadPending;
    private bool _suppressReactiveLoads;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRunLoading;
    [ObservableProperty] private bool _isGroupLoading;
    [ObservableProperty] private bool _isErrorLoading;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _isPreviewLoading;
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
    [ObservableProperty] private DuplicateMethodFilterOption? _selectedMethodFilter;
    [ObservableProperty] private DuplicateGroupSortBy _groupSortBy = DuplicateGroupSortBy.ReclaimableBytesDesc;
    [ObservableProperty] private bool _filterSelectedOnly;
    [ObservableProperty] private bool _filterMissingOnly;
    [ObservableProperty] private bool _filterErroredOnly;
    [ObservableProperty] private double _minimumConfidenceFilter;
    [ObservableProperty] private string _previewSummary = string.Empty;
    [ObservableProperty] private string _exportStatusText = string.Empty;
    [ObservableProperty] private string _lastExportDirectory = string.Empty;

    public ObservableCollection<ScanSession> Sessions { get; } = [];
    public ObservableCollection<DuplicateGroupItem> Groups { get; } = [];
    public ObservableCollection<DuplicateError> Errors { get; } = [];
    public ObservableCollection<DuplicatePreviewItem> PreviewItems { get; } = [];
    public ObservableCollection<QuarantinedFile> QuarantineItems { get; } = [];

    public Array KeeperPolicyOptions => Enum.GetValues(typeof(KeeperPolicy));
    public Array ScopeModeOptions => Enum.GetValues(typeof(DuplicateScopeMode));
    public Array GroupSortOptions => Enum.GetValues(typeof(DuplicateGroupSortBy));
    public ObservableCollection<DuplicateMethodFilterOption> MethodFilterOptions { get; } =
    [
        new("All methods", null),
        new("Exact", DuplicateMethod.ExactSha256),
        new("Normalized text", DuplicateMethod.NormalizedText),
        new("Image pHash", DuplicateMethod.ImagePHash),
        new("Video pHash", DuplicateMethod.VideoPHash),
    ];
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
    public bool HasPreviewItems => PreviewItems.Count > 0;
    public bool HasQuarantineItems => QuarantineItems.Count > 0;
    public bool IsWholeSessionScope => SelectedScopeMode == DuplicateScopeMode.WholeSession;
    public bool IsIncludedScopeMode => SelectedScopeMode == DuplicateScopeMode.IncludedFolders;
    public bool IsExcludedScopeMode => SelectedScopeMode == DuplicateScopeMode.ExcludedFolders;
    public bool IsCustomCategorySelected => SelectedCategory?.UsesCustomExtensions == true;
    public bool CanRun => SelectedSession is not null && !IsRunning && !IsDeleting && !IsRunLoading;
    public bool CanCancel => IsRunning;
    public bool CanDeleteSelected => _allGroups.Any(static group => group.SelectedCount > 0) && !IsDeleting && !IsRunning && !IsExporting && !IsRunLoading && !IsGroupLoading;
    public bool CanLoadPreviousGroups => _currentGroupPage > 1 && !IsLoading && !IsRunLoading && !IsGroupLoading && !IsRunning && !IsDeleting;
    public bool CanLoadNextGroups => _hasMoreGroupPages && !IsLoading && !IsRunLoading && !IsGroupLoading && !IsRunning && !IsDeleting;
    public bool CanLoadMoreErrors => _hasMoreErrorPages && !IsLoading && !IsRunLoading && !IsErrorLoading;
    public bool CanCancelExport => IsExporting;
    public bool CanChangeSession => !IsLoading && !IsRunLoading && !IsRunning && !IsDeleting && !IsExporting;
    public bool CanModifyDeletionPlan => !IsDeleting;
    public bool CanOpenExportFolder => !string.IsNullOrWhiteSpace(LastExportDirectory) && Directory.Exists(LastExportDirectory);
    public bool IsImagePHashAvailable => IsMethodAvailable(DuplicateMethod.ImagePHash);
    public bool IsVideoPHashAvailable => IsMethodAvailable(DuplicateMethod.VideoPHash);
    public string ProgressText => ProgressTotal <= 0 ? string.Empty : $"{ProgressProcessed:N0} / {ProgressTotal:N0}";
    public string ScopeHelpText => SelectedScopeMode switch
    {
        DuplicateScopeMode.IncludedFolders => "Only files inside listed folders are checked.",
        DuplicateScopeMode.ExcludedFolders => "Everything in scan is checked except listed folders and global exclusions.",
        _ => "Whole completed scan is checked.",
    };
    public int CurrentPage => _currentGroupPage;
    public int TotalGroupCount => (int)_runSummary.GroupCount;
    public int ExactGroupCount => (int)_runSummary.ExactGroupCount;
    public int ReviewGroupCount => (int)_runSummary.ReviewGroupCount;
    public string ReclaimableText => ByteSizeConverter.Format(_runSummary.ReclaimableBytes);
    public int SelectedDuplicateCount => _allGroups.Sum(static group => group.SelectedCount);
    public string SelectedDuplicateSummary => $"{SelectedDuplicateCount:N0} file(s) selected on current page";
    public string ErrorSummary => _runSummary.ErrorCount == 0 ? "No errors or skipped files." : $"{_runSummary.ErrorCount:N0} error / skipped item(s)";
    public string LatestRunSummary => LatestRun is null
        ? "No duplicate analysis has been saved for this scan session yet."
        : $"Last run: {LatestRun.Status} on {LatestRun.CompletedUtc?.ToString("g") ?? LatestRun.StartedUtc.ToString("g")}. " +
          $"{LatestRun.GroupCount:N0} group(s), {ByteSizeConverter.Format(LatestRun.ReclaimableBytes)} reclaimable, {LatestRun.ErrorCount:N0} error(s).";
    public string DeleteModeSummary => UseQuarantine
        ? "Delete mode: quarantine. Files can be restored below."
        : UseRecycleBin
            ? "Delete mode: Recycle Bin."
            : "Delete mode: permanent delete.";
    public string VideoMethodSummary => DescribeMethod(DuplicateMethod.VideoPHash,
        "Finds visually similar videos. Needs FFmpeg + ffprobe.");
    public string ImageMethodSummary => DescribeMethod(DuplicateMethod.ImagePHash,
        "Finds visually similar images.");

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
        IDuplicatePreviewService previewService,
        IDialogService dialogs,
        IEnumerable<IDuplicateDetectionStrategy> strategies)
    {
        _scanRepository = scanRepository;
        _settingsRepository = settingsRepository;
        _duplicateFinderService = duplicateFinderService;
        _duplicateRepository = duplicateRepository;
        _duplicateDeletionService = duplicateDeletionService;
        _previewService = previewService;
        _dialogs = dialogs;
        _strategyMap = strategies.ToDictionary(static s => s.Method);
        SelectedCategory = CategoryOptions.FirstOrDefault();
        SelectedMethodFilter = MethodFilterOptions.FirstOrDefault();
    }

    partial void OnSelectedSessionChanged(ScanSession? value)
    {
        OnPropertyChanged(nameof(CanRun));
        if (!_suppressReactiveLoads)
            _ = LoadLatestRunSafeAsync(value?.Id, CurrentLifetimeToken);
    }

    private async Task LoadLatestRunSafeAsync(long? sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await LoadLatestRunAsync(sessionId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && SelectedSession?.Id == sessionId)
                StatusText = $"Could not load duplicate runs: {ex.Message}";
            Debug.WriteLine(ex);
        }
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
        OnPropertyChanged(nameof(CanChangeSession));
    }

    partial void OnIsDeletingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanChangeSession));
        OnPropertyChanged(nameof(CanModifyDeletionPlan));
        OnPropertyChanged(nameof(CanLoadPreviousGroups));
        OnPropertyChanged(nameof(CanLoadNextGroups));
    }

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanCancelExport));
        OnPropertyChanged(nameof(CanOpenExportFolder));
        OnPropertyChanged(nameof(CanChangeSession));
    }

    partial void OnIsLoadingChanged(bool value) => RaiseLoadingProperties();
    partial void OnIsRunLoadingChanged(bool value) => RaiseLoadingProperties();
    partial void OnIsGroupLoadingChanged(bool value) => RaiseLoadingProperties();
    partial void OnIsErrorLoadingChanged(bool value) => RaiseLoadingProperties();

    partial void OnUseRecycleBinChanged(bool value)
    {
        if (value && UseQuarantine)
            UseQuarantine = false;
        OnPropertyChanged(nameof(DeleteModeSummary));
    }

    partial void OnUseQuarantineChanged(bool value)
    {
        if (value && UseRecycleBin)
            UseRecycleBin = false;
        OnPropertyChanged(nameof(DeleteModeSummary));
    }

    partial void OnSelectedScopeModeChanged(DuplicateScopeMode value)
    {
        OnPropertyChanged(nameof(IsWholeSessionScope));
        OnPropertyChanged(nameof(IsIncludedScopeMode));
        OnPropertyChanged(nameof(IsExcludedScopeMode));
        OnPropertyChanged(nameof(ScopeHelpText));
    }

    partial void OnSelectedCategoryChanged(DuplicateCategoryOption? value) =>
        OnPropertyChanged(nameof(IsCustomCategorySelected));

    partial void OnSearchTextChanged(string value) => QueueGroupReload();
    partial void OnSelectedMethodFilterChanged(DuplicateMethodFilterOption? value) => QueueGroupReload();
    partial void OnGroupSortByChanged(DuplicateGroupSortBy value) => QueueGroupReload();
    partial void OnFilterSelectedOnlyChanged(bool value) => QueueGroupReload();
    partial void OnFilterMissingOnlyChanged(bool value) => QueueGroupReload();
    partial void OnFilterErroredOnlyChanged(bool value) => QueueGroupReload();
    partial void OnMinimumConfidenceFilterChanged(double value) => QueueGroupReload();

    partial void OnSelectedGroupChanged(DuplicateGroupItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedGroup));
        if (!_suppressReactiveLoads)
            _ = LoadSelectedGroupDetailAsync(value);
    }

    partial void OnLastExportDirectoryChanged(string value) => OnPropertyChanged(nameof(CanOpenExportFolder));

    private void QueueGroupReload()
    {
        if (_suppressReactiveLoads || LatestRun is null || IsRunning)
            return;

        if (IsRunLoading)
        {
            _groupReloadPending = true;
            return;
        }

        _groupReloadPending = false;
        CancelAndDispose(ref _filterDebounceCts);
        _filterDebounceCts = CancellationTokenSource.CreateLinkedTokenSource(CurrentLifetimeToken);
        _ = DebouncedGroupReloadAsync(_filterDebounceCts.Token);
    }

    private async Task DebouncedGroupReloadAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            await ReloadCurrentGroupPageAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                StatusText = $"Could not apply duplicate filters: {ex.Message}";
            Debug.WriteLine(ex);
        }
    }

    public async Task InitializeAsync(
        long? preselectedSessionId = null,
        CancellationToken cancellationToken = default)
    {
        CancelBackgroundWork();
        var lifetimeVersion = ++_lifetimeVersion;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _lifetimeCts.Token;

        IsLoading = true;
        try
        {
            var settingsTask = _settingsRepository.LoadAsync(ct);
            var sessionsTask = _scanRepository.GetRecentSessionsAsync(50, ct);
            await Task.WhenAll(settingsTask, sessionsTask);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentLifetime(lifetimeVersion, ct))
                return;

            var settings = await settingsTask;
            var sessions = await sessionsTask;
            _suppressReactiveLoads = true;
            MinimumSizeMb = settings.DuplicateMinimumSizeMb;
            KeeperPolicy = settings.DuplicateKeeperPolicy;
            IncludeNormalizedText = settings.DuplicateUseNormalizedText && IsMethodAvailable(DuplicateMethod.NormalizedText);
            IncludeImagePHash = settings.DuplicateUseImagePHash && IsImagePHashAvailable;
            IncludeVideoPHash = settings.DuplicateUseVideoPHash && IsVideoPHashAvailable;
            UseRecycleBin = settings.PreferRecycleBin;
            UseQuarantine = false;
            IncludeHiddenFiles = settings.ShowHiddenFiles;
            IncludeReparsePoints = false;
            SelectedMethodFilter = MethodFilterOptions.FirstOrDefault(option =>
                string.Equals(option.Label, settings.DefaultDuplicatesReviewMode, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Method?.ToString(), settings.DefaultDuplicatesReviewMode, StringComparison.OrdinalIgnoreCase))
                ?? MethodFilterOptions.FirstOrDefault();

            Sessions.Clear();
            foreach (var session in sessions.Where(static session => session.Status == ScanStatus.Completed))
                Sessions.Add(session);

            SelectedSession = preselectedSessionId is long selectedId
                ? Sessions.FirstOrDefault(session => session.Id == selectedId) ?? Sessions.FirstOrDefault()
                : Sessions.FirstOrDefault();
            _suppressReactiveLoads = false;

            await LoadLatestRunAsync(SelectedSession?.Id, ct);
        }
        finally
        {
            _suppressReactiveLoads = false;
            if (IsCurrentLifetime(lifetimeVersion))
                IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RunAnalysisAsync()
    {
        var selectedSession = SelectedSession;
        var selectedCategory = SelectedCategory;
        if (selectedSession is null || selectedCategory is null)
        {
            // Never no-op silently on an enabled button.
            StatusText = "Select a scan session and file type before running duplicate analysis.";
            return;
        }

        var extensions = ParsePaths(CustomExtensionsText)
            .Select(static e => e.StartsWith('.') ? e : "." + e)
            .Select(static e => e.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selectedCategory.UsesCustomExtensions && extensions.Count == 0)
        {
            StatusText = "Enter at least one custom extension for custom-scope dedupe.";
            return;
        }

        var selectedScopeMode = SelectedScopeMode;
        var includedPaths = selectedScopeMode == DuplicateScopeMode.IncludedFolders
            ? ParsePaths(ScopePathsText)
            : [];
        if (selectedScopeMode == DuplicateScopeMode.IncludedFolders && includedPaths.Count == 0)
        {
            StatusText = "Add one or more folder prefixes for included-folder scope.";
            return;
        }

        var includeNormalizedText = IncludeNormalizedText;
        var includeImagePHash = IncludeImagePHash;
        var includeVideoPHash = IncludeVideoPHash;
        var includeHiddenFiles = IncludeHiddenFiles;
        var includeReparsePoints = IncludeReparsePoints;
        var minimumSizeMb = MinimumSizeMb;
        var keeperPolicy = KeeperPolicy;
        var excludedScopePathsText = ExcludedScopePathsText;
        var lifetimeVersion = _lifetimeVersion;

        CancelReviewLoads();
        var analysisCts = CancellationTokenSource.CreateLinkedTokenSource(CurrentLifetimeToken);
        _analysisCts = analysisCts;
        IsRunning = true;
        ProgressProcessed = 0;
        ProgressTotal = 0;
        ProgressPhase = string.Empty;
        StatusText = "Starting duplicate analysis…";
        ClearRunState();

        try
        {
            var settings = await _settingsRepository.LoadAsync(analysisCts.Token);

            var methods = new List<DuplicateMethod> { DuplicateMethod.ExactSha256 };
            if (includeNormalizedText && IsMethodAvailable(DuplicateMethod.NormalizedText))
                methods.Add(DuplicateMethod.NormalizedText);
            if (includeImagePHash && IsImagePHashAvailable)
                methods.Add(DuplicateMethod.ImagePHash);
            if (includeVideoPHash && IsVideoPHashAvailable)
                methods.Add(DuplicateMethod.VideoPHash);

            var excludedPaths = settings.ExcludedPaths.ToList();
            if (selectedScopeMode == DuplicateScopeMode.ExcludedFolders)
                excludedPaths.AddRange(ParsePaths(excludedScopePathsText));

            var completedRun = await _duplicateFinderService.RunAsync(
                new DuplicateScanOptions
                {
                    SessionId = selectedSession.Id,
                    MinimumSizeBytes = Math.Max(0, minimumSizeMb) * 1024L * 1024L,
                    Methods = methods,
                    KeeperPolicy = keeperPolicy,
                    MaxConcurrency = Math.Max(1, settings.ScanParallelism),
                    PerDriveConcurrency = Math.Max(1, Math.Min(4, settings.ScanParallelism / 2)),
                    IncludeExtensions = extensions,
                    IncludeCategories = selectedCategory.Categories,
                    IncludedPaths = includedPaths,
                    ExcludedPaths = excludedPaths,
                    IncludeHiddenFiles = includeHiddenFiles,
                    IncludeReparsePoints = includeReparsePoints,
                },
                new Progress<DuplicateDetectionProgress>(p =>
                {
                    if (!IsCurrentAnalysis(lifetimeVersion, analysisCts, selectedSession.Id))
                        return;

                    ProgressPhase = p.Phase;
                    ProgressProcessed = p.Processed;
                    ProgressTotal = Math.Max(p.Total, 1);
                    StatusText = $"{p.Phase}: {p.Processed:N0} / {p.Total:N0} ({p.GroupsFound:N0} groups, {p.Errors:N0} errors)";
                }),
                analysisCts.Token);

            analysisCts.Token.ThrowIfCancellationRequested();
            if (!IsCurrentAnalysis(lifetimeVersion, analysisCts, selectedSession.Id))
                return;

            await LoadLatestRunAsync(selectedSession.Id, CurrentLifetimeToken);
            if (!IsCurrentAnalysis(lifetimeVersion, analysisCts, selectedSession.Id))
                return;

            StatusText = completedRun.Status == DuplicateRunStatus.Cancelled
                ? "Analysis cancelled."
                : _runSummary.GroupCount > 0
                    ? $"Loaded page {_currentGroupPage:N0} of {_runSummary.GroupCount:N0} duplicate group(s). Review before deleting."
                    : "No duplicate groups found for current scope.";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentAnalysis(lifetimeVersion, analysisCts, selectedSession.Id))
                StatusText = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            if (IsCurrentAnalysis(lifetimeVersion, analysisCts, selectedSession.Id))
                StatusText = $"Duplicate analysis failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, analysisCts))
            {
                if (IsCurrentLifetime(lifetimeVersion))
                    IsRunning = false;
                _analysisCts = null;
            }
            analysisCts.Dispose();
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
        if (_currentGroupPage <= 1 || LatestRun is null || IsGroupLoading || IsDeleting)
            return;

        try
        {
            await LoadGroupsPageAsync(_currentGroupPage - 1, CurrentLifetimeToken);
            StatusText = $"Loaded page {_currentGroupPage:N0}.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load the previous duplicate page: {ex.Message}";
            Debug.WriteLine(ex);
        }
    }

    [RelayCommand]
    private async Task NextGroupsPageAsync()
    {
        if (!_hasMoreGroupPages || LatestRun is null || IsGroupLoading || IsDeleting)
            return;

        try
        {
            await LoadGroupsPageAsync(_currentGroupPage + 1, CurrentLifetimeToken);
            StatusText = $"Loaded page {_currentGroupPage:N0}.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load the next duplicate page: {ex.Message}";
            Debug.WriteLine(ex);
        }
    }

    [RelayCommand]
    private async Task LoadMoreErrorsAsync()
    {
        if (!_hasMoreErrorPages || LatestRun is null || IsErrorLoading)
            return;

        try
        {
            await LoadErrorsPageAsync(_currentErrorPage + 1, append: true, CurrentLifetimeToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load more duplicate errors: {ex.Message}";
            Debug.WriteLine(ex);
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (LatestRun is null)
            return;
        await RunExportAsync("csv", async (filePath, ct) =>
        {
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync("GroupId,Method,Confidence,KeeperPath,DuplicatePath,SizeBytes,Selected,ExistsNow");

            var page = 1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var groups = await _duplicateRepository.GetDuplicateGroupsPageAsync(
                    LatestRun.Id, page, GroupPageSize, null, DuplicateGroupSortBy.ReclaimableBytesDesc, ct);
                if (groups.Count == 0)
                    break;

                foreach (var group in groups)
                {
                    ct.ThrowIfCancellationRequested();
                    var members = await _duplicateRepository.GetDuplicateGroupMembersAsync(group.Id, ct);
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
                UpdateExportProgress(ct, $"Exporting CSV page {page - 1:N0}…");
            }
        });
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (LatestRun is null)
            return;
        await RunExportAsync("json", async (filePath, ct) =>
        {
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WritePropertyName("run");
            JsonSerializer.Serialize(writer, LatestRun);
            writer.WritePropertyName("summary");
            JsonSerializer.Serialize(writer, _runSummary);
            writer.WritePropertyName("settings");
            JsonSerializer.Serialize(writer, await _settingsRepository.LoadAsync(ct));

            writer.WritePropertyName("groups");
            writer.WriteStartArray();
            var page = 1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var groups = await _duplicateRepository.GetDuplicateGroupsPageAsync(
                    LatestRun.Id, page, GroupPageSize, null, DuplicateGroupSortBy.ReclaimableBytesDesc, ct);
                if (groups.Count == 0)
                    break;

                foreach (var group in groups)
                {
                    var members = await _duplicateRepository.GetDuplicateGroupMembersAsync(group.Id, ct);
                    writer.WriteStartObject();
                    writer.WriteNumber("id", group.Id);
                    writer.WriteString("method", group.Method.ToString());
                    writer.WriteNumber("confidence", group.Confidence);
                    writer.WriteNumber("totalBytes", group.TotalBytes);
                    writer.WriteNumber("reclaimableBytes", group.ReclaimableBytes);
                    writer.WritePropertyName("members");
                    JsonSerializer.Serialize(writer, members);
                    writer.WriteEndObject();
                }

                page++;
                UpdateExportProgress(ct, $"Exporting JSON page {page - 1:N0}…");
                await writer.FlushAsync(ct);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(ct);
        });
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (LatestRun is null)
            return;
        await RunExportAsync("html", async (filePath, ct) =>
        {
            await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync("<!doctype html>");
            await writer.WriteLineAsync("<html><head><meta charset=\"utf-8\"><title>StorageMaster Duplicate Report</title>");
            await writer.WriteLineAsync("<style>body{font-family:Segoe UI,Arial,sans-serif;padding:24px;}table{border-collapse:collapse;width:100%;}th,td{border:1px solid #ddd;padding:8px;}th{background:#f3f3f3;text-align:left;}tr:nth-child(even){background:#fafafa}</style>");
            await writer.WriteLineAsync("</head><body>");
            await writer.WriteLineAsync($"<h1>StorageMaster Duplicate Report (Run {LatestRun.Id})</h1>");
            await writer.WriteLineAsync($"<p>Generated {DateTime.Now:G}. Groups: {_runSummary.GroupCount:N0}, Reclaimable: {ByteSizeConverter.Format(_runSummary.ReclaimableBytes)}</p>");
            await writer.WriteLineAsync("<table><thead><tr><th>Group</th><th>Method</th><th>Confidence</th><th>Reclaimable</th><th>Keeper</th><th>Duplicate</th></tr></thead><tbody>");

            var page = 1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var groups = await _duplicateRepository.GetDuplicateGroupsPageAsync(
                    LatestRun.Id, page, GroupPageSize, null, DuplicateGroupSortBy.ReclaimableBytesDesc, ct);
                if (groups.Count == 0)
                    break;

                foreach (var group in groups)
                {
                    var members = await _duplicateRepository.GetDuplicateGroupMembersAsync(group.Id, ct);
                    var keeper = members.FirstOrDefault(static m => m.IsKeeper)?.FullPath ?? string.Empty;
                    foreach (var member in members.Where(static m => !m.IsKeeper))
                    {
                        await writer.WriteLineAsync("<tr>" +
                                                    $"<td>{EscapeHtml(group.Id.ToString())}</td>" +
                                                    $"<td>{EscapeHtml(group.Method.ToString())}</td>" +
                                                    $"<td>{EscapeHtml($"{group.Confidence:P0}")}</td>" +
                                                    $"<td>{EscapeHtml(ByteSizeConverter.Format(group.ReclaimableBytes))}</td>" +
                                                    $"<td>{EscapeHtml(keeper)}</td>" +
                                                    $"<td>{EscapeHtml(member.FullPath)}</td>" +
                                                    "</tr>");
                    }
                }

                page++;
                UpdateExportProgress(ct, $"Exporting HTML page {page - 1:N0}…");
            }

            await writer.WriteLineAsync("</tbody></table></body></html>");
        });
    }

    [RelayCommand]
    private void KeepNewest(DuplicateGroupItem group)
    {
        if (!CanModifyDeletionPlan)
            return;

        group.ApplyKeeperPolicy(KeeperPolicy.Newest);
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    [RelayCommand]
    private void KeepOldest(DuplicateGroupItem group)
    {
        if (!CanModifyDeletionPlan)
            return;

        group.ApplyKeeperPolicy(KeeperPolicy.Oldest);
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    [RelayCommand]
    private void KeepShortestPath(DuplicateGroupItem group)
    {
        if (!CanModifyDeletionPlan)
            return;

        group.ApplyKeeperPolicy(KeeperPolicy.ShortestPath);
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    [RelayCommand]
    private void KeepLongestPath(DuplicateGroupItem group)
    {
        if (!CanModifyDeletionPlan)
            return;

        group.ApplyKeeperPolicy(KeeperPolicy.LongestPath);
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    [RelayCommand]
    private void SelectSafeExactDuplicatesOnPage()
    {
        if (!CanModifyDeletionPlan)
            return;

        foreach (var group in _allGroups.Where(static item => item.Group.Method == DuplicateMethod.ExactSha256))
        {
            foreach (var member in group.Members)
                member.IsSelected = !member.IsKeeper && member.Member.ExistsNow;
        }

        StatusText = $"Selected exact-match duplicates on page {_currentGroupPage:N0}.";
        RaiseSummaryProperties();
    }

    [RelayCommand]
    private void DeselectCurrentPage()
    {
        if (!CanModifyDeletionPlan)
            return;

        foreach (var group in _allGroups)
        {
            foreach (var member in group.Members)
                member.IsSelected = false;
        }

        StatusText = $"Cleared selections on page {_currentGroupPage:N0}.";
        RaiseSummaryProperties();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var plan = CaptureDeletionPlan();
        if (plan is null)
            return;

        var lifetimeVersion = _lifetimeVersion;
        var selectedSessionId = SelectedSession?.Id;
        var methodLabel = plan.Method switch
        {
            DeletionMethod.Quarantine => "quarantine (restorable)",
            DeletionMethod.RecycleBin => "Recycle Bin",
            _ => "permanent deletion",
        };

        // Freeze the exact member/keeper/method plan and disable every editor before
        // yielding to the confirmation dialog. Execution never rereads mutable UI state.
        IsDeleting = true;
        try
        {
            var confirmed = await _dialogs.ConfirmAsync(
                "Delete selected duplicates from current page",
                $"Delete {plan.SelectedMemberCount:N0} duplicate file(s) from page {plan.Page:N0}?\n\nOne keeper per group stays untouched. Method: {methodLabel}.",
                $"Delete via {methodLabel}");
            if (!confirmed || !IsCurrentLifetime(lifetimeVersion))
                return;

            long processedBytes = 0;
            var processedFileCount = 0;
            var groupErrors = new List<string>();
            var persistenceWarnings = new List<string>();
            foreach (var groupPlan in plan.Groups)
            {
                // One group failing (e.g. its keeper vanished) must not abort
                // the remaining reviewed groups.
                try
                {
                    var result = await _duplicateDeletionService.DeleteSelectedWithResultAsync(
                        groupPlan.Group,
                        groupPlan.Members,
                        plan.Method);
                    processedBytes += result.ProcessedBytes;
                    processedFileCount += result.DeletedFileCount;
                    persistenceWarnings.AddRange(result.Warnings.Select(warning =>
                        $"Group {groupPlan.Group.Id}, {warning.Path}: {warning.Message}"));
                }
                catch (Exception ex)
                {
                    groupErrors.Add($"Group {groupPlan.Group.Id}: {ex.Message}");
                }
            }

            if (!IsCurrentLifetime(lifetimeVersion))
                return;

            var processedSize = ByteSizeConverter.Format(processedBytes);
            var operationSummary = plan.Method switch
            {
                DeletionMethod.Quarantine => $"Moved {processedSize} across {processedFileCount:N0} file(s) to quarantine",
                DeletionMethod.RecycleBin => $"Moved {processedSize} across {processedFileCount:N0} file(s) to the Recycle Bin",
                _ => $"Permanently deleted {processedSize} across {processedFileCount:N0} file(s)",
            };
            StatusText = (groupErrors.Count, persistenceWarnings.Count) switch
            {
                (0, 0) => $"{operationSummary} from page {plan.Page:N0}.",
                (0, _) => $"{operationSummary}; {persistenceWarnings.Count:N0} bookkeeping warning(s). First warning: {persistenceWarnings[0]}",
                (_, 0) => $"{operationSummary}; {groupErrors.Count:N0} group(s) failed. First error: {groupErrors[0]}",
                _ => $"{operationSummary}; {groupErrors.Count:N0} group(s) failed and {persistenceWarnings.Count:N0} bookkeeping warning(s). First issue: {groupErrors[0]}",
            };
            await LoadLatestRunAsync(selectedSessionId, CurrentLifetimeToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentLifetime(lifetimeVersion))
                StatusText = $"Duplicate deletion failed: {ex.Message}";
        }
        finally
        {
            if (IsCurrentLifetime(lifetimeVersion))
                IsDeleting = false;
        }
    }

    private DuplicateDeletionPlan? CaptureDeletionPlan()
    {
        var groups = _allGroups
            .Select(group => new DuplicateDeletionGroupPlan(
                group.Group,
                group.Members
                    .Select(member => member.Member with
                    {
                        IsSelected = member.IsSelected,
                        IsKeeper = member.IsKeeper,
                    })
                    .ToArray()))
            .Where(static group => group.Members.Any(static member => member.IsSelected && !member.IsKeeper))
            .ToArray();

        var selectedMemberCount = groups.Sum(static group =>
            group.Members.Count(static member => member.IsSelected && !member.IsKeeper));
        if (selectedMemberCount == 0)
            return null;

        var method = UseQuarantine ? DeletionMethod.Quarantine
            : UseRecycleBin ? DeletionMethod.RecycleBin
            : DeletionMethod.Permanent;
        return new DuplicateDeletionPlan(_currentGroupPage, method, selectedMemberCount, groups);
    }

    [RelayCommand]
    private async Task RestoreQuarantinedAsync(QuarantinedFile file)
    {
        try
        {
            await _duplicateDeletionService.RestoreFromQuarantineAsync(file.Id);
            await LoadLatestRunAsync(SelectedSession?.Id, CurrentLifetimeToken);
            StatusText = $"Restored {file.OriginalPath}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
            await _dialogs.ShowErrorAsync(
                "Restore failed",
                $"Could not restore \"{file.OriginalPath}\".\n\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelExport()
    {
        _exportCts?.Cancel();
        ExportStatusText = "Cancelling export…";
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        if (!CanOpenExportFolder)
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LastExportDirectory}\"")
        {
            UseShellExecute = true,
        });
    }

    private async Task LoadLatestRunAsync(long? sessionId, CancellationToken callerToken)
    {
        _exportCts?.Cancel();
        CancelAndDispose(ref _latestRunCts);
        var lifetimeVersion = _lifetimeVersion;
        var loadVersion = ++_latestRunVersion;
        _latestRunCts = CreateLifetimeLinkedTokenSource(callerToken);
        var ct = _latestRunCts.Token;

        CancelReviewLoads();
        ClearRunState();
        IsRunLoading = true;
        StatusText = "Loading duplicate review data…";
        try
        {
            // Quarantine is global (duplicate runs AND generic cleanup), so it is
            // shown even before a session or duplicate run exists.
            var quarantine = await _duplicateRepository.GetUnrestoredQuarantinedFilesAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentLatestRunLoad(lifetimeVersion, loadVersion, sessionId, ct))
                return;
            ReplaceQuarantine(quarantine);

            if (sessionId is null)
            {
                StatusText = "Choose a completed scan session to review duplicates.";
                return;
            }

            var runs = await _duplicateRepository.GetRunsForSessionAsync(sessionId.Value, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentLatestRunLoad(lifetimeVersion, loadVersion, sessionId, ct))
                return;

            var latestRun = runs.FirstOrDefault();
            if (latestRun is null)
            {
                StatusText = "No duplicate analysis has been run for this scan yet.";
                return;
            }

            var summary = await _duplicateRepository.GetDuplicateRunSummaryAsync(latestRun.Id, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentLatestRunLoad(lifetimeVersion, loadVersion, sessionId, ct))
                return;

            LatestRun = latestRun;
            _runSummary = summary;
            OnPropertyChanged(nameof(HasLatestRun));
            OnPropertyChanged(nameof(LatestRunSummary));
            RaiseSummaryProperties();

            await LoadGroupsPageAsync(1, ct);
            await LoadErrorsPageAsync(1, append: false, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentLatestRunLoad(lifetimeVersion, loadVersion, sessionId, ct))
                return;

            StatusText = _runSummary.GroupCount > 0
                ? $"Loaded page {_currentGroupPage:N0} of duplicate groups ({_runSummary.GroupCount:N0} total)."
                : "Latest duplicate analysis completed without duplicate groups.";
        }
        finally
        {
            if (IsCurrentLatestRunLoad(lifetimeVersion, loadVersion, sessionId))
            {
                IsRunLoading = false;
                if (_groupReloadPending)
                    QueueGroupReload();
            }
        }
    }

    private async Task ReloadCurrentGroupPageAsync(CancellationToken ct)
    {
        if (LatestRun is null || IsRunning)
            return;

        await LoadGroupsPageAsync(_currentGroupPage, ct);
    }

    private async Task LoadGroupsPageAsync(int page, CancellationToken ct)
    {
        var latestRun = LatestRun;
        if (latestRun is null)
            return;

        CancelAndDispose(ref _groupReloadCts);
        var lifetimeVersion = _lifetimeVersion;
        var loadVersion = ++_groupReloadVersion;
        _groupReloadCts = CreateLifetimeLinkedTokenSource(ct);
        var linkedToken = _groupReloadCts.Token;

        var filter = new DuplicateGroupQueryFilter
        {
            SearchText = SearchText,
            Method = SelectedMethodFilter?.Method,
            MinConfidence = MinimumConfidenceFilter > 0 ? MinimumConfidenceFilter : null,
            HasSelectedMembers = FilterSelectedOnly ? true : null,
            ExistsNow = FilterMissingOnly ? false : null,
            IncludeErroredOnly = FilterErroredOnly,
        };
        var sortBy = GroupSortBy;
        var keeperPolicy = KeeperPolicy;

        IsGroupLoading = true;
        try
        {
            var result = await FetchGroupPageAsync(
                latestRun.Id,
                page,
                filter,
                sortBy,
                keeperPolicy,
                linkedToken);
            linkedToken.ThrowIfCancellationRequested();
            if (!IsCurrentGroupLoad(lifetimeVersion, loadVersion, latestRun.Id, linkedToken))
                return;

            ApplyGroupPage(result);
        }
        finally
        {
            if (IsCurrentGroupLoad(lifetimeVersion, loadVersion, latestRun.Id))
                IsGroupLoading = false;
        }
    }

    private void OnMemberPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(CanDeleteSelected));

    private async Task LoadErrorsPageAsync(
        int page,
        bool append,
        CancellationToken callerToken = default)
    {
        var latestRun = LatestRun;
        if (latestRun is null)
            return;

        CancelAndDispose(ref _errorReloadCts);
        var lifetimeVersion = _lifetimeVersion;
        var loadVersion = ++_errorReloadVersion;
        _errorReloadCts = CreateLifetimeLinkedTokenSource(callerToken);
        var ct = _errorReloadCts.Token;

        IsErrorLoading = true;
        try
        {
            var result = await FetchErrorPageAsync(latestRun.Id, page, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentErrorLoad(lifetimeVersion, loadVersion, latestRun.Id, ct))
                return;

            _currentErrorPage = result.Page;
            _hasMoreErrorPages = result.HasMore;
            if (!append)
                Errors.Clear();
            foreach (var error in result.Items)
                Errors.Add(error);
            RaiseSummaryProperties();
        }
        finally
        {
            if (IsCurrentErrorLoad(lifetimeVersion, loadVersion, latestRun.Id))
                IsErrorLoading = false;
        }
    }

    private async Task LoadSelectedGroupDetailAsync(DuplicateGroupItem? selectedGroup)
    {
        CancelAndDispose(ref _previewLoadCts);
        var lifetimeVersion = _lifetimeVersion;
        var loadVersion = ++_previewLoadVersion;
        _previewLoadCts = CreateLifetimeLinkedTokenSource(CancellationToken.None);
        var ct = _previewLoadCts.Token;

        PreviewItems.Clear();
        PreviewSummary = string.Empty;

        var latestRun = LatestRun;
        if (selectedGroup is null || latestRun is null)
        {
            RaiseSummaryProperties();
            return;
        }

        IsPreviewLoading = true;
        try
        {
            var preview = await _previewService.BuildPreviewAsync(
                selectedGroup.Group.Method,
                selectedGroup.Members.Select(static member => member.Member).ToList(),
                ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentPreviewLoad(
                    lifetimeVersion,
                    loadVersion,
                    latestRun.Id,
                    selectedGroup,
                    ct))
                return;

            PreviewSummary = preview.Summary;
            foreach (var item in preview.Items)
                PreviewItems.Add(item);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentPreviewLoad(lifetimeVersion, loadVersion, latestRun.Id, selectedGroup))
                PreviewSummary = $"Could not load preview: {ex.Message}";
            Debug.WriteLine(ex);
        }
        finally
        {
            if (IsCurrentPreviewLoad(lifetimeVersion, loadVersion, latestRun.Id, selectedGroup))
            {
                IsPreviewLoading = false;
                RaiseSummaryProperties();
            }
        }
    }

    private async Task<GroupPageResult> FetchGroupPageAsync(
        long runId,
        int requestedPage,
        DuplicateGroupQueryFilter filter,
        DuplicateGroupSortBy sortBy,
        KeeperPolicy keeperPolicy,
        CancellationToken ct)
    {
        var page = Math.Max(1, requestedPage);
        var groups = (await _duplicateRepository.GetDuplicateGroupsPageAsync(
            runId,
            page,
            GroupPageSize + 1,
            filter,
            sortBy,
            ct)).ToList();

        if (groups.Count == 0 && page > 1)
        {
            page = 1;
            groups = (await _duplicateRepository.GetDuplicateGroupsPageAsync(
                runId,
                page,
                GroupPageSize + 1,
                filter,
                sortBy,
                ct)).ToList();
        }

        var hasMore = groups.Count > GroupPageSize;
        if (hasMore)
            groups = groups.Take(GroupPageSize).ToList();

        var items = new List<DuplicateGroupItem>(groups.Count);
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var members = await _duplicateRepository.GetDuplicateGroupMembersAsync(group.Id, ct);
            var item = new DuplicateGroupItem(group, members);
            item.ApplyKeeperPolicy(keeperPolicy);
            items.Add(item);
        }

        return new GroupPageResult(page, hasMore, items);
    }

    private async Task<ErrorPageResult> FetchErrorPageAsync(
        long runId,
        int requestedPage,
        CancellationToken ct)
    {
        var page = Math.Max(1, requestedPage);
        var errors = (await _duplicateRepository.GetDuplicateErrorsPageAsync(
            runId,
            page,
            ErrorPageSize + 1,
            ct)).ToList();
        var hasMore = errors.Count > ErrorPageSize;
        if (hasMore)
            errors = errors.Take(ErrorPageSize).ToList();
        return new ErrorPageResult(page, hasMore, errors);
    }

    private void ApplyGroupPage(GroupPageResult result)
    {
        DetachGroupHandlers();
        Groups.Clear();
        PreviewItems.Clear();
        PreviewSummary = string.Empty;
        _allGroups.Clear();

        _currentGroupPage = result.Page;
        _hasMoreGroupPages = result.HasMore;
        foreach (var item in result.Items)
        {
            foreach (var member in item.Members)
                member.PropertyChanged += OnMemberPropertyChanged;
            _allGroups.Add(item);
            Groups.Add(item);
        }

        SelectedGroup = Groups.FirstOrDefault();
        RaiseSummaryProperties();
    }

    private void ReplaceQuarantine(IReadOnlyList<QuarantinedFile> items)
    {
        QuarantineItems.Clear();
        foreach (var item in items)
            QuarantineItems.Add(item);
        OnPropertyChanged(nameof(HasQuarantineItems));
    }

    private void ClearRunState()
    {
        DetachGroupHandlers();
        var wasSuppressing = _suppressReactiveLoads;
        _suppressReactiveLoads = true;
        SelectedGroup = null;
        _suppressReactiveLoads = wasSuppressing;

        Groups.Clear();
        Errors.Clear();
        PreviewItems.Clear();
        QuarantineItems.Clear();
        PreviewSummary = string.Empty;
        _allGroups.Clear();
        LatestRun = null;
        _runSummary = new DuplicateRunSummary();
        _currentGroupPage = 1;
        _hasMoreGroupPages = false;
        _currentErrorPage = 1;
        _hasMoreErrorPages = false;
        OnPropertyChanged(nameof(HasLatestRun));
        OnPropertyChanged(nameof(LatestRunSummary));
        RaiseSummaryProperties();
    }

    private void DetachGroupHandlers()
    {
        foreach (var group in _allGroups)
            foreach (var member in group.Members)
                member.PropertyChanged -= OnMemberPropertyChanged;
    }

    private async Task RunExportAsync(string extension, Func<string, CancellationToken, Task> exportAction)
    {
        if (LatestRun is null || IsExporting)
            return;

        var lifetimeVersion = _lifetimeVersion;
        var exportDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster",
            "exports");
        Directory.CreateDirectory(exportDir);
        var filePath = Path.Combine(exportDir, $"dedupe-{LatestRun.Id}-report.{extension}");

        _exportCts?.Cancel();
        var exportCts = CancellationTokenSource.CreateLinkedTokenSource(CurrentLifetimeToken);
        _exportCts = exportCts;
        var ct = exportCts.Token;

        IsExporting = true;
        ExportStatusText = $"Exporting {extension.ToUpperInvariant()} report…";
        try
        {
            await exportAction(filePath, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsCurrentExport(lifetimeVersion, exportCts))
                return;

            LastExportDirectory = exportDir;
            ExportStatusText = $"{extension.ToUpperInvariant()} export saved to {filePath}.";
            StatusText = ExportStatusText;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentExport(lifetimeVersion, exportCts))
            {
                ExportStatusText = "Export cancelled.";
                StatusText = ExportStatusText;
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentExport(lifetimeVersion, exportCts))
            {
                ExportStatusText = $"Export failed: {ex.Message}";
                StatusText = ExportStatusText;
            }
        }
        finally
        {
            if (ReferenceEquals(_exportCts, exportCts))
            {
                if (IsCurrentLifetime(lifetimeVersion))
                    IsExporting = false;
                _exportCts = null;
            }
            exportCts.Dispose();
        }
    }

    private void UpdateExportProgress(CancellationToken ct, string status)
    {
        if (!ct.IsCancellationRequested && _exportCts?.Token == ct)
            ExportStatusText = status;
    }

    public void CancelBackgroundWork()
    {
        _lifetimeVersion++;
        _latestRunVersion++;
        CancelReviewLoads();
        CancelAndDispose(ref _latestRunCts);
        CancelAndDispose(ref _lifetimeCts);
        _analysisCts?.Cancel();
        _exportCts?.Cancel();
        IsLoading = false;
        IsRunLoading = false;
        IsRunning = false;
        IsDeleting = false;
        IsExporting = false;
    }

    private void CancelReviewLoads()
    {
        _groupReloadVersion++;
        _errorReloadVersion++;
        _previewLoadVersion++;
        _groupReloadPending = false;
        CancelAndDispose(ref _filterDebounceCts);
        CancelAndDispose(ref _groupReloadCts);
        CancelAndDispose(ref _errorReloadCts);
        CancelAndDispose(ref _previewLoadCts);
        IsGroupLoading = false;
        IsErrorLoading = false;
        IsPreviewLoading = false;
    }

    private CancellationToken CurrentLifetimeToken =>
        _lifetimeCts?.Token ?? CancellationToken.None;

    private CancellationTokenSource CreateLifetimeLinkedTokenSource(CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(CurrentLifetimeToken, callerToken);

    private bool IsCurrentLifetime(long lifetimeVersion) =>
        lifetimeVersion == _lifetimeVersion;

    private bool IsCurrentLifetime(long lifetimeVersion, CancellationToken ct) =>
        !ct.IsCancellationRequested && IsCurrentLifetime(lifetimeVersion);

    private bool IsCurrentAnalysis(
        long lifetimeVersion,
        CancellationTokenSource analysisCts,
        long sessionId) =>
        IsCurrentLifetime(lifetimeVersion) &&
        ReferenceEquals(_analysisCts, analysisCts) &&
        SelectedSession?.Id == sessionId;

    private bool IsCurrentExport(long lifetimeVersion, CancellationTokenSource exportCts) =>
        IsCurrentLifetime(lifetimeVersion) && ReferenceEquals(_exportCts, exportCts);

    private bool IsCurrentLatestRunLoad(
        long lifetimeVersion,
        long loadVersion,
        long? sessionId) =>
        IsCurrentLifetime(lifetimeVersion) &&
        loadVersion == _latestRunVersion &&
        SelectedSession?.Id == sessionId;

    private bool IsCurrentLatestRunLoad(
        long lifetimeVersion,
        long loadVersion,
        long? sessionId,
        CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        IsCurrentLatestRunLoad(lifetimeVersion, loadVersion, sessionId);

    private bool IsCurrentGroupLoad(
        long lifetimeVersion,
        long loadVersion,
        long runId) =>
        IsCurrentLifetime(lifetimeVersion) &&
        loadVersion == _groupReloadVersion &&
        LatestRun?.Id == runId;

    private bool IsCurrentGroupLoad(
        long lifetimeVersion,
        long loadVersion,
        long runId,
        CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        IsCurrentGroupLoad(lifetimeVersion, loadVersion, runId);

    private bool IsCurrentErrorLoad(
        long lifetimeVersion,
        long loadVersion,
        long runId) =>
        IsCurrentLifetime(lifetimeVersion) &&
        loadVersion == _errorReloadVersion &&
        LatestRun?.Id == runId;

    private bool IsCurrentErrorLoad(
        long lifetimeVersion,
        long loadVersion,
        long runId,
        CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        IsCurrentErrorLoad(lifetimeVersion, loadVersion, runId);

    private bool IsCurrentPreviewLoad(
        long lifetimeVersion,
        long loadVersion,
        long runId,
        DuplicateGroupItem selectedGroup) =>
        IsCurrentLifetime(lifetimeVersion) &&
        loadVersion == _previewLoadVersion &&
        LatestRun?.Id == runId &&
        ReferenceEquals(SelectedGroup, selectedGroup);

    private bool IsCurrentPreviewLoad(
        long lifetimeVersion,
        long loadVersion,
        long runId,
        DuplicateGroupItem selectedGroup,
        CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        IsCurrentPreviewLoad(lifetimeVersion, loadVersion, runId, selectedGroup);

    private void RaiseLoadingProperties()
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanLoadPreviousGroups));
        OnPropertyChanged(nameof(CanLoadNextGroups));
        OnPropertyChanged(nameof(CanLoadMoreErrors));
        OnPropertyChanged(nameof(CanChangeSession));
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
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
        OnPropertyChanged(nameof(HasPreviewItems));
        OnPropertyChanged(nameof(HasQuarantineItems));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(TotalGroupCount));
        OnPropertyChanged(nameof(ExactGroupCount));
        OnPropertyChanged(nameof(ReviewGroupCount));
        OnPropertyChanged(nameof(ReclaimableText));
        OnPropertyChanged(nameof(SelectedDuplicateCount));
        OnPropertyChanged(nameof(SelectedDuplicateSummary));
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

    private string DescribeMethod(DuplicateMethod method, string readyText)
    {
        if (!_strategyMap.TryGetValue(method, out var strategy))
            return "Unavailable.";

        return strategy.IsAvailable
            ? readyText
            : strategy.UnavailableReason ?? "Unavailable.";
    }
}
