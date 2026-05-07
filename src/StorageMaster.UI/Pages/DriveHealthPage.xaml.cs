using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class DriveHealthPage : Page
{
    public DriveHealthViewModel ViewModel { get; }

    public DriveHealthPage()
    {
        ViewModel = App.Services.GetRequiredService<DriveHealthViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Drive health failed to load: {ex.Message}";
        }
    }
}
