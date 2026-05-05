using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class DuplicateMemberItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isKeeper;

    public DuplicateGroupMember Member { get; }
    public string SizeText => ByteSizeConverter.Format(Member.SizeBytes);
    public string ModifiedText => Member.ModifiedUtc.ToString("g");

    public DuplicateMemberItem(DuplicateGroupMember member)
    {
        Member = member;
        _isSelected = member.IsSelected;
        _isKeeper = member.IsKeeper;
    }
}

public sealed partial class DuplicateGroupItem : ObservableObject
{
    public DuplicateGroup Group { get; }
    public ObservableCollection<DuplicateMemberItem> Members { get; } = [];
    public string MethodText => Group.Method.ToString();
    public string TotalSizeText => ByteSizeConverter.Format(Group.TotalBytes);
    public string ReclaimableText => ByteSizeConverter.Format(Group.ReclaimableBytes);

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

    public int SelectedCount => Members.Count(static member => member.IsSelected && !member.IsKeeper);

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

        foreach (var member in Members)
        {
            member.IsKeeper = ReferenceEquals(member, keeper);
            member.IsSelected = !member.IsKeeper && member.Member.ExistsNow;
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    private void OnMemberPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DuplicateMemberItem.IsSelected) or nameof(DuplicateMemberItem.IsKeeper))
            OnPropertyChanged(nameof(SelectedCount));
    }
}

public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly IScanRepository _scanRepository;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IDuplicateFinderService _duplicateFinderService;
    private readonly IDuplicateRepository _duplicateRepository;
    private readonly IDuplicateDeletionService _duplicateDeletionService;
    private readonly IDialogService _dialogs;
    private readonly HashSet<DuplicateMethod> _availableMethods;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private string _statusText = "Choose scan session, run duplicate analysis, review groups, delete copies.";
    [ObservableProperty] private int _minimumSizeMb = 1;
    [ObservableProperty] private bool _useRecycleBin = true;
    [ObservableProperty] private bool _includeNormalizedText;
    [ObservableProperty] private bool _includeImagePHash;
    [ObservableProperty] private bool _includeVideoPHash;
    [ObservableProperty] private KeeperPolicy _keeperPolicy = KeeperPolicy.Newest;
    [ObservableProperty] private ScanSession? _selectedSession;
    [ObservableProperty] private DuplicateRun? _latestRun;

    public ObservableCollection<ScanSession> Sessions { get; } = [];
    public ObservableCollection<DuplicateGroupItem> Groups { get; } = [];
    public Array KeeperPolicyOptions => Enum.GetValues(typeof(KeeperPolicy));

    public bool HasGroups => Groups.Count > 0;
    public bool HasLatestRun => LatestRun is not null;
    public bool CanRun => SelectedSession is not null && !IsRunning && !IsDeleting;
    public bool CanDeleteSelected => Groups.Any(static group => group.SelectedCount > 0) && !IsDeleting && !IsRunning;
    public bool IsImagePHashAvailable => _availableMethods.Contains(DuplicateMethod.ImagePHash);
    public bool IsVideoPHashAvailable => _availableMethods.Contains(DuplicateMethod.VideoPHash);
    public string LatestRunSummary => LatestRun is null
        ? "No duplicate analysis has been saved for this scan session yet."
        : $"Last run: {LatestRun.Status} on {LatestRun.CompletedUtc?.ToString("g") ?? LatestRun.StartedUtc.ToString("g")}. " +
          $"{LatestRun.GroupCount:N0} group(s), {ByteSizeConverter.Format(LatestRun.ReclaimableBytes)} reclaimable, {LatestRun.ErrorCount:N0} error(s).";
    public string ProviderSummary => $"Methods available in this build: exact SHA-256{(_availableMethods.Contains(DuplicateMethod.NormalizedText) ? ", normalized text" : string.Empty)}.";

    public DuplicatesViewModel(
        IScanRepository scanRepository,
        ISettingsRepository settingsRepository,
        IDuplicateFinderService duplicateFinderService,
        IDuplicateRepository duplicateRepository,
        IDuplicateDeletionService duplicateDeletionService,
        IDialogService dialogs,
        IEnumerable<IDuplicateSignatureProvider> signatureProviders)
    {
        _scanRepository = scanRepository;
        _settingsRepository = settingsRepository;
        _duplicateFinderService = duplicateFinderService;
        _duplicateRepository = duplicateRepository;
        _duplicateDeletionService = duplicateDeletionService;
        _dialogs = dialogs;
        _availableMethods = signatureProviders.Select(static provider => provider.Method).ToHashSet();
    }

    partial void OnSelectedSessionChanged(ScanSession? value)
    {
        OnPropertyChanged(nameof(CanRun));
        _ = LoadLatestRunAsync();
    }

    partial void OnKeeperPolicyChanged(KeeperPolicy value)
    {
        foreach (var group in Groups)
            group.ApplyKeeperPolicy(value);

        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    partial void OnIsDeletingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    public async Task InitializeAsync(long? preselectedSessionId = null)
    {
        IsLoading = true;
        try
        {
            var settings = await _settingsRepository.LoadAsync();
            MinimumSizeMb = settings.DuplicateMinimumSizeMb;
            KeeperPolicy = settings.DuplicateKeeperPolicy;
            IncludeNormalizedText = settings.DuplicateUseNormalizedText && _availableMethods.Contains(DuplicateMethod.NormalizedText);
            IncludeImagePHash = settings.DuplicateUseImagePHash && IsImagePHashAvailable;
            IncludeVideoPHash = settings.DuplicateUseVideoPHash && IsVideoPHashAvailable;
            UseRecycleBin = settings.PreferRecycleBin;

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
        if (SelectedSession is null)
            return;

        IsRunning = true;
        StatusText = "Analyzing exact duplicates…";
        Groups.Clear();

        try
        {
            var methods = new List<DuplicateMethod> { DuplicateMethod.ExactSha256 };
            if (IncludeNormalizedText && _availableMethods.Contains(DuplicateMethod.NormalizedText))
                methods.Add(DuplicateMethod.NormalizedText);
            if (IncludeImagePHash && IsImagePHashAvailable)
                methods.Add(DuplicateMethod.ImagePHash);
            if (IncludeVideoPHash && IsVideoPHashAvailable)
                methods.Add(DuplicateMethod.VideoPHash);

            LatestRun = await _duplicateFinderService.RunAsync(
                new DuplicateScanOptions
                {
                    SessionId = SelectedSession.Id,
                    MinimumSizeBytes = Math.Max(0, MinimumSizeMb) * 1024L * 1024L,
                    Methods = methods,
                    KeeperPolicy = KeeperPolicy,
                    MaxConcurrency = 4,
                },
                new Progress<DuplicateDetectionProgress>(p =>
                {
                    StatusText = $"{p.Stage}: {p.ProcessedFiles:N0}/{p.TotalFiles:N0}";
                }));

            await LoadLatestRunAsync();
            StatusText = Groups.Count > 0
                ? $"Found {Groups.Count:N0} duplicate group(s). Review before deleting."
                : "No duplicate groups found for current scan.";
        }
        catch (Exception ex)
        {
            StatusText = $"Duplicate analysis failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
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
        var selectedGroups = Groups.Where(static group => group.SelectedCount > 0).ToList();
        if (selectedGroups.Count == 0)
            return;

        var selectedMembers = selectedGroups.SelectMany(static group => group.Members).Count(static member => member.IsSelected && !member.IsKeeper);
        var confirmed = await _dialogs.ConfirmAsync(
            "Delete selected duplicates",
            $"Delete {selectedMembers:N0} duplicate file(s)?\n\nOne keeper per group stays untouched. Default action uses {(UseRecycleBin ? "Recycle Bin" : "permanent deletion")}.",
            UseRecycleBin ? "Delete selected" : "Permanently delete");

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
                    UseRecycleBin ? DeletionMethod.RecycleBin : DeletionMethod.Permanent);
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
        LatestRun = null;
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasLatestRun));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(LatestRunSummary));

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

        var groups = await _duplicateRepository.GetGroupsForRunAsync(LatestRun.Id);
        foreach (var group in groups)
        {
            var members = await _duplicateRepository.GetMembersForGroupAsync(group.Id);
            var item = new DuplicateGroupItem(group, members);
            foreach (var member in item.Members)
                member.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanDeleteSelected));
            Groups.Add(item);
        }

        foreach (var group in Groups)
            group.ApplyKeeperPolicy(KeeperPolicy);

        StatusText = Groups.Count > 0
            ? $"Loaded {Groups.Count:N0} group(s) from the latest duplicate analysis."
            : "Latest duplicate analysis completed without duplicate groups.";
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }
}
