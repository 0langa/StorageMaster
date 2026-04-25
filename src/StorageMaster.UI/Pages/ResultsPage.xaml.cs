using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class ResultsPage : Page
{
    public ResultsViewModel ViewModel { get; }

    public ResultsPage()
    {
        ViewModel = App.Services.GetRequiredService<ResultsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // The session ID is passed as navigation parameter; fall back to most recent.
        if (e.Parameter is long sessionId && sessionId > 0)
        {
            await ViewModel.LoadAsync(sessionId);
        }
    }
}
