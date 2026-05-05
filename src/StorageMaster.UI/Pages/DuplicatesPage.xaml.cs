using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class DuplicatesPage : Page
{
    public DuplicatesViewModel ViewModel { get; }

    public DuplicatesPage()
    {
        ViewModel = App.Services.GetRequiredService<DuplicatesViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.InitializeAsync(e.Parameter is long sessionId ? sessionId : null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
