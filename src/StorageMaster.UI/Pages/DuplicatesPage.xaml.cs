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

    /// <summary>x:Bind helper: labels where a quarantined file came from.</summary>
    public static string DescribeQuarantineSource(long? memberId) =>
        memberId is null ? "· Cleanup quarantine" : "· Duplicate quarantine";

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
