using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class DuplicatesPage : Page
{
    private CancellationTokenSource? _navigationCts;

    public DuplicatesViewModel ViewModel { get; }

    public DuplicatesPage()
    {
        ViewModel = App.Services.GetRequiredService<DuplicatesViewModel>();
        InitializeComponent();
    }

    /// <summary>x:Bind helper: labels where a quarantined file came from.</summary>
    public static string DescribeQuarantineSource(long? memberId) =>
        memberId is null ? "· Cleanup quarantine" : "· Duplicate quarantine";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        try
        {
            await ViewModel.InitializeAsync(
                e.Parameter is long sessionId ? sessionId : null,
                _navigationCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
        ViewModel.CancelBackgroundWork();
        base.OnNavigatedFrom(e);
    }
}
