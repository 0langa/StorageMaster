using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class SmartCleanerPage : Page
{
    public SmartCleanerViewModel ViewModel { get; }

    public SmartCleanerPage()
    {
        ViewModel = App.Services.GetRequiredService<SmartCleanerViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }
}
