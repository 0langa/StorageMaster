using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Pages;

public sealed partial class DriveHealthViewModel(
    IDriveHealthProvider provider,
    IDriveHealthRepository repository) : ObservableObject
{
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = Loc.Get("Health_Status_Initial");
    [ObservableProperty] private DriveHealthSnapshot? _selectedSnapshot;

    public ObservableCollection<DriveHealthSnapshot> Snapshots { get; } = [];
    public bool HasSnapshots => Snapshots.Count > 0;

    public async Task LoadAsync()
    {
        var latest = await repository.GetLatestSnapshotsAsync();
        ReplaceSnapshots(latest);
        if (latest.Count > 0)
            StatusText = Loc.Get("Health_Status_ShowingStored");
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
            // The count is formatted in the user's culture before composition:
            // Loc.Format composes invariantly so it is not formatted twice.
            StatusText = snapshots.Count == 0
                ? Loc.Get("Health_Status_NoDrives")
                : Loc.Format(
                    "Health_Status_Captured",
                    snapshots.Count.ToString("N0", CultureInfo.CurrentCulture));
        }
        catch (Exception ex)
        {
            StatusText = Loc.Format("Health_Error_RefreshFailed", ex.Message);
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
