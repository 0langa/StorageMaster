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

    /// <summary>
    /// Asks a question about a list of items and shows the items under it. The close
    /// button — the answer that changes nothing — is always the default, so Enter
    /// or Esc never confirms.
    /// </summary>
    Task<bool> ConfirmItemsAsync(
        string title,
        string message,
        IReadOnlyList<string> items,
        string primaryButtonText,
        string closeButtonText);

    Task ShowErrorAsync(
        string title,
        string message,
        string closeButtonText = "OK");
}
