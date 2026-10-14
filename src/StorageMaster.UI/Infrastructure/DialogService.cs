using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StorageMaster.Core.Localization;

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

    /// <summary>
    /// Items listed in full before the list is cut short. A refusal list can run to
    /// hundreds of paths; past this the dialog would only scroll, and the count in
    /// the message already says how many there are.
    /// </summary>
    internal const int MaxListedItems = 200;

    public async Task<bool> ConfirmItemsAsync(
        string title,
        string message,
        IReadOnlyList<string> items,
        string primaryButtonText,
        string closeButtonText)
    {
        var list = new StackPanel { Spacing = 2 };
        foreach (var item in items.Take(MaxListedItems))
        {
            list.Children.Add(new TextBlock
            {
                Text = item,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }

        if (items.Count > MaxListedItems)
        {
            list.Children.Add(new TextBlock
            {
                Text = Loc.Format(
                    "Safety_RecycleRefused_More",
                    (items.Count - MaxListedItems).ToString("N0", CultureInfo.CurrentCulture)),
                FontSize = 12,
                Opacity = 0.7,
            });
        }

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        content.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        var result = await ShowAsync(new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
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
