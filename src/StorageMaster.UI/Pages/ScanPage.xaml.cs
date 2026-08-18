using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StorageMaster.UI.Pages;

public sealed partial class ScanPage : Page
{
    private bool _pickerOpen;
    private CancellationTokenSource? _initializationCancellation;
    public ScanViewModel ViewModel { get; }

    public ScanPage()
    {
        ViewModel = App.Services.GetRequiredService<ScanViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _initializationCancellation?.Cancel();
        var initializationCancellation = new CancellationTokenSource();
        _initializationCancellation = initializationCancellation;
        var cancellationToken = initializationCancellation.Token;
        try
        {
            // Don't reinitialise while a scan is running (would reset live progress)
            // or after it completes (would clear the completion banner and View Results button).
            // The user must explicitly start a new scan to reset state.
            if (!ViewModel.IsScanning && !ViewModel.ScanComplete)
                await ViewModel.InitializeAsync(
                    autoEnableDeepScan: App.StartWithDeepScan,
                    preselectedPath: e.Parameter as string,
                    cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer navigation owns the singleton view-model initialization.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            if (ReferenceEquals(_initializationCancellation, initializationCancellation) &&
                !ViewModel.IsScanning)
            {
                ViewModel.HasError = true;
                ViewModel.ErrorMessage = $"Scan page failed to initialize: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_initializationCancellation, initializationCancellation))
                _initializationCancellation = null;
            initializationCancellation.Dispose();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _initializationCancellation?.Cancel();
        _initializationCancellation = null;
        base.OnNavigatedFrom(e);
    }

    private async void BrowseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_pickerOpen)
            return;

        _pickerOpen = true;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            // WinUI 3 requires the HWND to be associated with the picker.
            var hwnd = WindowNative.GetWindowHandle(App.Services.GetRequiredService<MainWindow>());
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && ViewModel.CanBrowse)
                ViewModel.SelectedPath = folder.Path;
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Could not open folder picker: {ex.Message}";
            ViewModel.HasError = true;
        }
        finally
        {
            _pickerOpen = false;
        }
    }

    private void DriveButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (ViewModel.CanBrowse && sender is Button btn && btn.Tag is string driveName)
            ViewModel.SelectedPath = driveName;
    }
}
