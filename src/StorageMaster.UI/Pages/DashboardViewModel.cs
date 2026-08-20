using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IScanRepository _repo;
    private readonly IDriveInfoProvider _drives;
    private readonly IDriveHealthProvider _driveHealthProvider;
    private readonly IDriveHealthRepository _driveHealthRepository;
    private readonly INavigationService _nav;

    [ObservableProperty] private ScanSession? _lastSession;
    [ObservableProperty] private ScanSession? _latestAttempt;
    [ObservableProperty] private string _totalScannedSize = "—";
    [ObservableProperty] private long _totalFiles;
    [ObservableProperty] private string _statusMessage = Loc.Get("Dashboard_Status_NoScanYet");
    [ObservableProperty] private bool _hasLastSession;
    [ObservableProperty] private IReadOnlyList<DriveDetail> _drives2 = [];
    [ObservableProperty] private IReadOnlyList<DriveHealthSnapshot> _driveHealthSnapshots = [];
    [ObservableProperty] private string _heroTitle = Loc.Get("Dashboard_Hero_Title_Default");
    [ObservableProperty] private string _heroSubtitle = Loc.Get("Dashboard_Hero_Subtitle_Default");
    [ObservableProperty] private string _recommendedActionText = Loc.Get("Dashboard_Action_RunScan");
    [ObservableProperty] private string _latestScanSummary = Loc.Get("Dashboard_LatestScan_None");
    [ObservableProperty] private string _driveHealthSummary = string.Empty;
    [ObservableProperty] private double _readinessScore;
    [ObservableProperty] private string _readinessLabel = Loc.Get("Dashboard_Readiness_Label");
    [ObservableProperty] private string _readinessDescription = Loc.Get("Dashboard_Readiness_Description_Default");

    public ObservableCollection<string> Recommendations { get; } = [];

    public bool HasDrives => Drives2.Count > 0;
    public bool IsFirstRun => !HasLastSession;
    public bool HasRecommendations => Recommendations.Count > 0;

    public DashboardViewModel(
        IScanRepository repo,
        IDriveInfoProvider drives,
        IDriveHealthProvider driveHealthProvider,
        IDriveHealthRepository driveHealthRepository,
        INavigationService nav)
    {
        _repo = repo;
        _drives = drives;
        _driveHealthProvider = driveHealthProvider;
        _driveHealthRepository = driveHealthRepository;
        _nav = nav;
    }

    public async Task LoadAsync()
    {
        Drives2 = _drives.GetAvailableDrives();
        DriveHealthSnapshots = await LoadDriveHealthAsync();
        OnPropertyChanged(nameof(HasDrives));
        Recommendations.Clear();

        // The dashboard needs two distinct concepts: the latest attempt for
        // diagnostics and the latest completed session that is safe to open in
        // downstream result/cleanup workflows. Loading metadata for the complete
        // history keeps that selection exact even after several failed attempts.
        var sessions = await _repo.GetRecentSessionsAsync(int.MaxValue);
        LatestAttempt = sessions.FirstOrDefault();
        LastSession = sessions.FirstOrDefault(static session => session.Status == ScanStatus.Completed);
        HasLastSession = LastSession is not null;
        var lowSpaceDrives = Drives2.Where(static d => d.IsReady && d.TotalBytes > 0)
            .Select(static drive => new
            {
                Drive = drive,
                FreePercent = (int)Math.Round((double)drive.FreeBytes / drive.TotalBytes * 100d),
            })
            .Where(static x => x.FreePercent <= 15)
            .ToList();

        var healthIssues = DriveHealthSnapshots.Count(static d =>
            d.Status is DriveHealthStatus.Warning or DriveHealthStatus.Critical);
        DriveHealthSummary = BuildDriveHealthSummary(lowSpaceDrives.Count, healthIssues);

        if (LastSession is not null)
        {
            TotalFiles = LastSession.TotalFiles;
            TotalScannedSize = ByteSizeConverter.Format(LastSession.TotalSizeBytes);
            if (LatestAttempt is { Status: not ScanStatus.Completed } latestAttempt)
            {
                LatestScanSummary = Loc.Format(
                    "Dashboard_LatestScan_AttemptAndCompleted",
                    Loc.Get(EnumDisplayConverter.KeyFor(latestAttempt.Status)),
                    LastSession.RootPath,
                    FormatSessionTime(LastSession));
                StatusMessage = Loc.Format(
                    "Dashboard_Status_LatestIncomplete",
                    Loc.Get(EnumDisplayConverter.KeyFor(latestAttempt.Status)),
                    LastSession.RootPath);
                HeroTitle = Loc.Get("Dashboard_Hero_Title_NeedsAttention");
                HeroSubtitle = Loc.Get("Dashboard_Hero_Subtitle_NeedsAttention");
                RecommendedActionText = Loc.Get("Dashboard_Action_RetryScan");
                Recommendations.Add(Loc.Format("Dashboard_Recommendation_RetryLatest", Loc.Get(EnumDisplayConverter.KeyFor(latestAttempt.Status))));
            }
            else
            {
                LatestScanSummary = Loc.Format(
                    "Dashboard_LatestScan_Completed",
                    LastSession.RootPath,
                    FormatSessionTime(LastSession));
                StatusMessage = Loc.Format(
                    "Dashboard_Status_LastScanCompleted",
                    LastSession.RootPath,
                    FormatSessionTime(LastSession));
                HeroTitle = Loc.Get("Dashboard_Hero_Title_Ready");
                HeroSubtitle = Loc.Get("Dashboard_Hero_Subtitle_Ready");
                RecommendedActionText = lowSpaceDrives.Count > 0
                    ? Loc.Get("Dashboard_Action_RunCleanup")
                    : Loc.Get("Dashboard_Action_OpenLatestResults");
            }

            if (LastSession.CompletedUtc is null || LastSession.CompletedUtc < DateTime.UtcNow.AddDays(-14))
                Recommendations.Add(Loc.Get("Dashboard_Recommendation_StaleScan"));
            if (lowSpaceDrives.Count > 0)
                Recommendations.Add(DriveHealthSummary);
            if (healthIssues > 0)
                Recommendations.Add(Loc.Get("Dashboard_Recommendation_ReviewDriveHealth"));
            Recommendations.Add(Loc.Get("Safety_ReviewDuplicatesBeforeDeleting"));
        }
        else
        {
            TotalFiles = 0;
            TotalScannedSize = "—";
            HeroTitle = LatestAttempt is null
                ? Loc.Get("Dashboard_Hero_Title_FirstRun")
                : Loc.Get("Dashboard_Hero_Title_NoCompletedScan");
            HeroSubtitle = LatestAttempt is null
                ? Loc.Get("Dashboard_Hero_Subtitle_FirstRun")
                : Loc.Format("Dashboard_Hero_Subtitle_NoCompletedScan", LatestAttempt.Status);
            RecommendedActionText = Loc.Get("Dashboard_Action_StartScan");
            LatestScanSummary = LatestAttempt is null
                ? Loc.Get("Dashboard_LatestScan_None")
                : Loc.Format(
                    "Dashboard_LatestScan_AttemptOnly",
                    Loc.Get(EnumDisplayConverter.KeyFor(LatestAttempt.Status)),
                    FormatSessionTime(LatestAttempt));
            StatusMessage = LatestAttempt is null
                ? HasDrives
                    ? Loc.Get("Dashboard_Status_NoCompletedScan")
                    : Loc.Get("Dashboard_Status_NoDrives")
                : Loc.Format("Dashboard_Status_NoCompletedScanAttempt", LatestAttempt.Status);
            if (HasDrives)
                Recommendations.Add(LatestAttempt is null
                    ? Loc.Get("Dashboard_Recommendation_FirstScan")
                    : Loc.Get("Dashboard_Recommendation_RetryIncomplete"));
            if (lowSpaceDrives.Count > 0)
                Recommendations.Add(DriveHealthSummary);
            if (healthIssues > 0)
                Recommendations.Add(Loc.Get("Dashboard_Recommendation_ReviewDriveHealthFirstCleanup"));
        }

        UpdateReadinessScore(lowSpaceDrives.Count, healthIssues, LatestAttempt);
        OnPropertyChanged(nameof(IsFirstRun));
        OnPropertyChanged(nameof(HasRecommendations));
    }

    [RelayCommand]
    private void GoToScan() => _nav.NavigateTo(typeof(ScanPage));

    [RelayCommand]
    private void ScanDrive(DriveDetail drive) => _nav.NavigateTo(typeof(ScanPage), drive.Name);

    [RelayCommand]
    private void GoToWorkspace()
    {
        if (HasLastSession)
            _nav.NavigateTo(typeof(ScanWorkspacePage), LastSession!.Id);
        else
            _nav.NavigateTo(typeof(ScanWorkspacePage));
    }

    [RelayCommand]
    private void GoToResults()
    {
        if (HasLastSession)
            _nav.NavigateTo(typeof(ResultsPage), LastSession!.Id);
    }

    [RelayCommand]
    private void GoToDuplicates()
    {
        if (HasLastSession)
            _nav.NavigateTo(typeof(DuplicatesPage), LastSession!.Id);
    }

    [RelayCommand]
    private void GoToCleanup()
    {
        if (HasLastSession)
            _nav.NavigateTo(typeof(CleanupPage), LastSession!.Id);
        else
            _nav.NavigateTo(typeof(ScanPage));
    }

    [RelayCommand]
    private void GoToSmartCleaner() => _nav.NavigateTo(typeof(SmartCleanerPage));

    [RelayCommand]
    private void GoToSpaceMap()
    {
        if (HasLastSession)
            _nav.NavigateTo(typeof(SpaceMapPage), LastSession!.Id);
        else
            _nav.NavigateTo(typeof(SpaceMapPage));
    }

    [RelayCommand]
    private void GoToDriveHealth() => _nav.NavigateTo(typeof(DriveHealthPage));

    [RelayCommand]
    private void GoToSettings() => _nav.NavigateTo(typeof(SettingsPage));

    private async Task<IReadOnlyList<DriveHealthSnapshot>> LoadDriveHealthAsync()
    {
        try
        {
            var snapshots = await _driveHealthProvider.GetHealthAsync();
            await _driveHealthRepository.SaveSnapshotsAsync(snapshots);
            return snapshots;
        }
        catch
        {
            return await _driveHealthRepository.GetLatestSnapshotsAsync();
        }
    }

    private static string BuildDriveHealthSummary(int lowSpaceCount, int healthIssueCount)
    {
        if (lowSpaceCount == 0 && healthIssueCount == 0)
            return Loc.Get("Dashboard_DriveHealth_AllClear");

        var parts = new List<string>();
        if (lowSpaceCount > 0)
            parts.Add(Loc.Format(
                "Dashboard_DriveHealth_LowSpace",
                lowSpaceCount.ToString("N0", CultureInfo.CurrentCulture)));
        if (healthIssueCount > 0)
            parts.Add(Loc.Format(
                "Dashboard_DriveHealth_Warnings",
                healthIssueCount.ToString("N0", CultureInfo.CurrentCulture)));
        return string.Join("; ", parts) + ".";
    }

    private void UpdateReadinessScore(
        int lowSpaceCount,
        int healthIssueCount,
        ScanSession? latestAttempt)
    {
        var score = 100;
        if (!HasLastSession)
            score -= 35;
        else if (LastSession is { CompletedUtc: null } || LastSession!.CompletedUtc < DateTime.UtcNow.AddDays(-14))
            score -= 15;

        if (latestAttempt is { Status: not ScanStatus.Completed })
            score -= 25;

        if (lowSpaceCount > 0)
            score -= Math.Min(35, lowSpaceCount * 20);
        if (healthIssueCount > 0)
            score -= Math.Min(35, healthIssueCount * 25);

        ReadinessScore = Math.Clamp(score, 0, 100);
        ReadinessLabel = Loc.Get("Dashboard_Readiness_Label");
        ReadinessDescription = Loc.Format(
            "Dashboard_Readiness_Description",
            ReadinessScore.ToString("N0", CultureInfo.CurrentCulture));
    }

    private static string FormatSessionTime(ScanSession session) =>
        (session.CompletedUtc ?? session.StartedUtc).ToLocalTime().ToString("g");
}
