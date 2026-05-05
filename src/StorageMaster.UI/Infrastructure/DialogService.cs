using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace StorageMaster.UI.Infrastructure;

public sealed class DialogService(MainWindow window) : IDialogService
{
    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText = "Cancel",
        ContentDialogButton defaultButton = ContentDialogButton.Close)
    {
        var result = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = defaultButton,
        }).ConfigureAwait(true);

        return result == ContentDialogResult.Primary;
    }

    public async Task ShowErrorAsync(
        string title,
        string message,
        string closeButtonText = "OK")
    {
        await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
        }).ConfigureAwait(true);
    }

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (window.Content is not FrameworkElement root || root.XamlRoot is null)
            throw new InvalidOperationException("Dialog service requires an active window root.");

        dialog.XamlRoot = root.XamlRoot;
        return await dialog.ShowAsync();
    }
}
