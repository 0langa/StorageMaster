using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class ScanWorkspacePage : Page
{
    public ScanWorkspaceViewModel ViewModel { get; }

    public ScanWorkspacePage()
    {
        ViewModel = App.Services.GetRequiredService<ScanWorkspaceViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var sessionId = e.Parameter switch
        {
            long id => id,
            int id => id,
            _ => (long?)null,
        };
        await ViewModel.LoadAsync(sessionId);
    }
}
