using Microsoft.UI.Xaml.Controls;

namespace StorageMaster.UI.Infrastructure;

public interface IDialogService
{
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText,
        string closeButtonText = "Cancel",
        ContentDialogButton defaultButton = ContentDialogButton.Close);

    Task ShowErrorAsync(
        string title,
        string message,
        string closeButtonText = "OK");
}
