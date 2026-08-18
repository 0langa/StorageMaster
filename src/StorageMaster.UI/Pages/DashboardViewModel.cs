using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
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
    [ObservableProperty] private string _statusMessage = "No scan yet. Start a scan to analyse your disk.";
    [ObservableProperty] private bool _hasLastSession;
    [ObservableProperty] private IReadOnlyList<DriveDetail> _drives2 = [];
    [ObservableProperty] private IReadOnlyList<DriveHealthSnapshot> _driveHealthSnapshots = [];
    [ObservableProperty] private string _heroTitle = "Storage overview";
    [ObservableProperty] private string _heroSubtitle = "Pick a quick action to start scanning, cleanup, or duplicate review.";
    [ObservableProperty] private string _recommendedActionText = "Run a scan";
    [ObservableProperty] private string _latestScanSummary = "No scan history yet.";
    [ObservableProperty] private string _driveHealthSummary = string.Empty;
    [ObservableProperty] private double _readinessScore;
    [ObservableProperty] private string _readinessLabel = "health score";
    [ObservableProperty] private string _readinessDescription = "Score combines scan freshness, low-space pressure, and drive-health warnings.";

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
                LatestScanSummary =
                    $"Latest scan attempt: {latestAttempt.Status}. Last completed scan: {LastSession.RootPath} on {FormatSessionTime(LastSession)}.";
                StatusMessage =
                    $"The latest scan did not complete ({latestAttempt.Status}); actionable views use the earlier completed scan of {LastSession.RootPath}.";
                HeroTitle = "Latest scan needs attention";
                HeroSubtitle = "Retry the scan. Previous completed results remain available for read-only review.";
                RecommendedActionText = "Retry scan";
                Recommendations.Add($"Retry the latest scan; it ended with status {latestAttempt.Status}.");
            }
            else
            {
                LatestScanSummary = $"Last completed scan: {LastSession.RootPath} on {FormatSessionTime(LastSession)}";
                StatusMessage = $"Last scan of {LastSession.RootPath} completed {FormatSessionTime(LastSession)}";
                HeroTitle = "Storage ready";
                HeroSubtitle = "Jump back into the latest completed scan, duplicates, or cleanup without waiting for a cold load.";
                RecommendedActionText = lowSpaceDrives.Count > 0 ? "Run Cleanup" : "Open latest Results";
            }

            if (LastSession.CompletedUtc is null || LastSession.CompletedUtc < DateTime.UtcNow.AddDays(-14))
                Recommendations.Add("Run a fresh scan. The latest scan is stale.");
            if (lowSpaceDrives.Count > 0)
                Recommendations.Add(DriveHealthSummary);
            if (healthIssues > 0)
                Recommendations.Add("Review drive health before running large cleanup or duplicate operations.");
            Recommendations.Add("Review duplicates before deleting anything large or old.");
        }
        else
        {
            TotalFiles = 0;
            TotalScannedSize = "—";
            HeroTitle = LatestAttempt is null ? "First run" : "No completed scan";
            HeroSubtitle = LatestAttempt is null
                ? "Start with a scan, then StorageMaster can guide cleanup and duplicate review safely."
                : $"The latest scan ended with status {LatestAttempt.Status}. Retry it before opening downstream actions.";
            RecommendedActionText = "Start a scan";
            LatestScanSummary = LatestAttempt is null
                ? "No scan history yet."
                : $"Latest scan attempt: {LatestAttempt.Status} at {FormatSessionTime(LatestAttempt)}.";
            StatusMessage = LatestAttempt is null
                ? HasDrives
                    ? "No completed scan yet. Start with a drive below or open a custom path."
                    : "No drives detected. Connect storage, then start a scan."
                : $"No completed scan is available. The latest attempt is {LatestAttempt.Status}.";
            if (HasDrives)
                Recommendations.Add(LatestAttempt is null
                    ? "Run a first scan so results, duplicate review, and cleanup can use real data."
                    : "Retry the scan; incomplete scan data is not exposed to cleanup or duplicate actions.");
            if (lowSpaceDrives.Count > 0)
                Recommendations.Add(DriveHealthSummary);
            if (healthIssues > 0)
                Recommendations.Add("Review drive health before starting your first cleanup.");
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
            return "No drives are currently under the low-space threshold or reporting health warnings.";

        var parts = new List<string>();
        if (lowSpaceCount > 0)
            parts.Add($"{lowSpaceCount:N0} drive(s) are running low on free space");
        if (healthIssueCount > 0)
            parts.Add($"{healthIssueCount:N0} drive(s) report health warnings");
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
        ReadinessLabel = "health score";
        ReadinessDescription =
            $"Storage health score: {ReadinessScore:N0}/100 from scan freshness, low-space threshold, and drive-health warnings.";
    }

    private static string FormatSessionTime(ScanSession session) =>
        (session.CompletedUtc ?? session.StartedUtc).ToLocalTime().ToString("g");
}
