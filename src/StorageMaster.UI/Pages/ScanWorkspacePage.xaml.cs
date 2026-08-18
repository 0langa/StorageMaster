using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class ScanWorkspacePage : Page
{
    private CancellationTokenSource? _loadCancellation;

    public ScanWorkspaceViewModel ViewModel { get; }

    public ScanWorkspacePage()
    {
        ViewModel = App.Services.GetRequiredService<ScanWorkspaceViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _loadCancellation?.Cancel();
        var loadCancellation = new CancellationTokenSource();
        _loadCancellation = loadCancellation;
        var cancellationToken = loadCancellation.Token;

        var sessionId = e.Parameter switch
        {
            long id => id,
            int id => id,
            _ => (long?)null,
        };

        try
        {
            await ViewModel.LoadAsync(sessionId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Navigation cancelled this page's load. A newer page owns visible state.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_loadCancellation, loadCancellation))
                ViewModel.StatusText = $"Workspace failed to load: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, loadCancellation))
                _loadCancellation = null;
            loadCancellation.Dispose();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        base.OnNavigatedFrom(e);
    }
}
