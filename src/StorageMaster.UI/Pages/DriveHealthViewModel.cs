using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Pages;

public sealed partial class DriveHealthViewModel(
    IDriveHealthProvider provider,
    IDriveHealthRepository repository) : ObservableObject
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "Refresh drive health to read Windows storage telemetry.";
    [ObservableProperty] private DriveHealthSnapshot? _selectedSnapshot;

    public ObservableCollection<DriveHealthSnapshot> Snapshots { get; } = [];
    public bool HasSnapshots => Snapshots.Count > 0;

    public async Task LoadAsync()
    {
        var latest = await repository.GetLatestSnapshotsAsync();
        ReplaceSnapshots(latest);
        if (latest.Count > 0)
            StatusText = "Showing the latest stored drive health snapshot.";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
            return;

        IsLoading = true;
        try
        {
            var snapshots = await provider.GetHealthAsync();
            await repository.SaveSnapshotsAsync(snapshots);
            ReplaceSnapshots(snapshots);
            StatusText = snapshots.Count == 0
                ? "No ready local drives were found."
                : $"Captured {snapshots.Count:N0} drive health snapshot(s).";
        }
        catch (Exception ex)
        {
            StatusText = $"Drive health refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ReplaceSnapshots(IReadOnlyList<DriveHealthSnapshot> snapshots)
    {
        Snapshots.Clear();
        foreach (var snapshot in snapshots)
            Snapshots.Add(snapshot);
        SelectedSnapshot = Snapshots.FirstOrDefault();
        OnPropertyChanged(nameof(HasSnapshots));
    }
}
