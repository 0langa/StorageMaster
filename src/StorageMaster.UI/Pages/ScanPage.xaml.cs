using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StorageMaster.UI.Pages;

public sealed partial class ScanPage : Page
{
    public ScanViewModel ViewModel { get; }

    public ScanPage()
    {
        ViewModel = App.Services.GetRequiredService<ScanViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Don't reinitialise while a scan is running — that would reset the live
        // progress state. Allow reinit once scanning finishes so the drive list
        // and default path stay fresh on subsequent visits.
        if (!ViewModel.IsScanning)
            await ViewModel.InitializeAsync(autoEnableDeepScan: App.StartWithDeepScan);
    }

    private async void BrowseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        // WinUI 3 requires the HWND to be associated with the picker.
        var hwnd = WindowNative.GetWindowHandle(App.Services.GetRequiredService<MainWindow>());
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ViewModel.SelectedPath = folder.Path;
    }

    private void DriveButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string driveName)
            ViewModel.SelectedPath = driveName;
    }
}
