using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.UI.Pages;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IScanRepository    _repo;
    private readonly IDriveInfoProvider _drives;
    private readonly INavigationService _nav;

    [ObservableProperty] private ScanSession?   _lastSession;
    [ObservableProperty] private string         _totalScannedSize = "—";
    [ObservableProperty] private long            _totalFiles;
    [ObservableProperty] private string          _statusMessage    = "No scan yet. Start a scan to analyse your disk.";
    [ObservableProperty] private bool            _hasLastSession;
    [ObservableProperty] private IReadOnlyList<DriveDetail> _drives2 = [];

    public DashboardViewModel(
        IScanRepository    repo,
        IDriveInfoProvider drives,
        INavigationService nav)
    {
        _repo   = repo;
        _drives = drives;
        _nav    = nav;
    }

    public async Task LoadAsync()
    {
        Drives2 = _drives.GetAvailableDrives();

        var sessions = await _repo.GetRecentSessionsAsync(count: 1);
        if (sessions.Count > 0)
        {
            LastSession      = sessions[0];
            TotalFiles       = LastSession.TotalFiles;
            TotalScannedSize = ByteSizeConverter.Format(LastSession.TotalSizeBytes);
            HasLastSession   = true;
            StatusMessage    = LastSession.Status == ScanStatus.Completed
                ? $"Last scan of {LastSession.RootPath} completed {LastSession.CompletedUtc:g}"
                : $"Last scan status: {LastSession.Status}";
        }
    }

    [RelayCommand]
    private void GoToScan() => _nav.NavigateTo(typeof(ScanPage));

    [RelayCommand]
    private void GoToResults()
    {
        if (HasLastSession)
            _nav.NavigateTo(typeof(ResultsPage), LastSession!.Id);
    }
}
