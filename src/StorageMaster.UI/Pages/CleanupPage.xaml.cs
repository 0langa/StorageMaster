using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace StorageMaster.UI.Pages;

public sealed partial class CleanupPage : Page
{
    public CleanupViewModel ViewModel { get; }

    public CleanupPage()
    {
        ViewModel = App.Services.GetRequiredService<CleanupViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        // Safety confirmation before any deletion.
        var dialog = new ContentDialog
        {
            Title           = ViewModel.IsDryRun ? "Confirm Dry Run" : "Confirm Cleanup",
            Content         = ViewModel.IsDryRun
                ? "This will simulate the cleanup without deleting anything. Continue?"
                : $"This will delete the selected items (total: {ViewModel.TotalSelectedSize}). " +
                  "Files will be sent to the Recycle Bin where possible. Continue?",
            PrimaryButtonText   = ViewModel.IsDryRun ? "Run Preview" : "Clean Up",
            CloseButtonText     = "Cancel",
            DefaultButton       = ContentDialogButton.Close,
            XamlRoot            = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.UpdateTotalSelected();
            await ViewModel.ExecuteCleanupCommand.ExecuteAsync(null);
        }
    }
}
