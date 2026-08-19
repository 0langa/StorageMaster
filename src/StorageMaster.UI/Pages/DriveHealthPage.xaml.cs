using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using StorageMaster.Core.Localization;

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
            ViewModel.StatusText = Loc.Format("Health_Error_LoadFailed", ex.Message);
        }
    }
}
