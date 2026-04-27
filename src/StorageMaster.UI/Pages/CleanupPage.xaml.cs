using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using StorageMaster.Core.Interfaces;

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

    // ── "Clean Up Selected…" button ────────────────────────────────────────

    private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.UpdateTotalSelected();

        var isDryRun = ViewModel.IsDryRun;
        var size     = ViewModel.TotalSelectedSize;

        string title   = isDryRun ? "Confirm Dry Run Preview" : "Confirm Cleanup";
        string message = isDryRun
            ? $"This will simulate the cleanup for {size} of selected items without deleting anything. Continue?"
            : $"This will delete {size} of selected files and folders. " +
              "Items will be sent to the Recycle Bin if that setting is enabled, " +
              "otherwise they will be permanently deleted. Continue?";

        var confirm = new ContentDialog
        {
            Title               = title,
            Content             = message,
            PrimaryButtonText   = isDryRun ? "Run Preview" : "Clean Up",
            CloseButtonText     = "Cancel",
            DefaultButton       = ContentDialogButton.Close,
            XamlRoot            = XamlRoot,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        await ViewModel.ExecuteCleanupCommand.ExecuteAsync(null);
        await ShowReportLoopAsync();
    }

    // ── Report dialog loop ─────────────────────────────────────────────────

    /// <summary>
    /// Shows the cleanup report. If the user chooses to re-run with a different
    /// deletion mode (e.g. after a dry run) the loop runs the engine again and
    /// shows a fresh report — at most three passes (dry → recycle → permanent).
    /// </summary>
    private async Task ShowReportLoopAsync()
    {
        while (true)
        {
            bool           wasDry   = ViewModel.LastRunWasDryRun;
            DeletionMethod wasMethod = ViewModel.LastRunDeletionMethod;
            var            results  = ViewModel.ExecutionResults.ToList();
            string         summary  = ViewModel.LastRunSummary;

            var dialog = BuildReportDialog(wasDry, wasMethod, results, summary);
            var choice = await dialog.ShowAsync();

            if (choice == ContentDialogResult.Primary && wasDry)
            {
                // "Delete (Recycle Bin)" — first real run, use RecycleBin
                await ViewModel.RunCleanupWithMethodAsync(dryRun: false, DeletionMethod.RecycleBin);
            }
            else if (choice == ContentDialogResult.Secondary && wasDry)
            {
                // "Delete Permanently" — skip recycle bin altogether
                await ViewModel.RunCleanupWithMethodAsync(dryRun: false, DeletionMethod.Permanent);
            }
            else if (choice == ContentDialogResult.Primary && !wasDry && wasMethod == DeletionMethod.RecycleBin)
            {
                // "Delete Permanently" — upgrade from recycle-bin run
                await ViewModel.RunCleanupWithMethodAsync(dryRun: false, DeletionMethod.Permanent);
            }
            else
            {
                break; // User dismissed, or no further action available.
            }
        }
    }

    // ── Dialog builder ─────────────────────────────────────────────────────

    private ContentDialog BuildReportDialog(
        bool                            isDryRun,
        DeletionMethod                  method,
        IReadOnlyList<CleanupResultDisplay> results,
        string                          summary)
    {
        // ── Content ────────────────────────────────────────────────────────

        var mainStack = new StackPanel { Spacing = 16 };

        // Summary row
        if (!string.IsNullOrWhiteSpace(summary))
        {
            mainStack.Children.Add(new TextBlock
            {
                Text        = summary,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity     = 0.85,
            });
        }

        // Header row
        if (results.Count > 0)
        {
            var header = BuildResultRow("Item", "Status", isDryRun ? "Est. size" : "Freed", isHeader: true);
            mainStack.Children.Add(header);

            var divider = new Border
            {
                Height     = 1,
                Opacity    = 0.2,
                Background = new SolidColorBrush(Colors.Gray),
                Margin     = new Thickness(0, 0, 0, 4),
            };
            mainStack.Children.Add(divider);
        }

        // Per-item rows
        foreach (var r in results)
        {
            bool ok = r.Status is "Success" or "PartialSuccess";
            bool skipped = r.Status is "Skipped";

            var titleText = new TextBlock
            {
                Text         = r.Title,
                FontWeight   = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var titleCell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            titleCell.Children.Add(titleText);

            if (!string.IsNullOrWhiteSpace(r.Error))
            {
                titleCell.Children.Add(new TextBlock
                {
                    Text      = r.Error,
                    FontSize  = 11,
                    Opacity   = 0.7,
                    Foreground = new SolidColorBrush(Colors.OrangeRed),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            var statusText = new TextBlock
            {
                Text              = r.Status,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                FontSize          = 12,
                Opacity           = ok ? 0.9 : 0.6,
                Foreground        = ok
                    ? new SolidColorBrush(Colors.MediumSeaGreen)
                    : skipped
                        ? new SolidColorBrush(Colors.Gray)
                        : new SolidColorBrush(Colors.OrangeRed),
            };

            var sizeText = new TextBlock
            {
                Text                = r.WasDryRun ? $"~{r.BytesFreed}" : r.BytesFreed,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
                FontWeight          = FontWeights.SemiBold,
                FontSize            = 12,
            };

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            Grid.SetColumn(titleCell,  0);
            Grid.SetColumn(statusText, 1);
            Grid.SetColumn(sizeText,   2);
            row.Children.Add(titleCell);
            row.Children.Add(statusText);
            row.Children.Add(sizeText);
            mainStack.Children.Add(row);
        }

        var scrollContent = new ScrollViewer
        {
            MaxHeight                   = 380,
            Content                     = mainStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // ── Dialog ─────────────────────────────────────────────────────────

        string title = isDryRun
            ? "Dry Run Report — No files were deleted"
            : method == DeletionMethod.RecycleBin
                ? "Cleanup Report — Items sent to Recycle Bin"
                : "Cleanup Report — Items permanently deleted";

        var dialog = new ContentDialog
        {
            Title         = title,
            Content       = scrollContent,
            CloseButtonText = "Close",
            XamlRoot      = XamlRoot,
            DefaultButton = ContentDialogButton.Close,
        };

        // Add action buttons depending on what the last run was.
        if (isDryRun)
        {
            dialog.PrimaryButtonText   = "Delete (Recycle Bin)";
            dialog.SecondaryButtonText = "Delete Permanently";
            dialog.DefaultButton       = ContentDialogButton.Primary;
        }
        else if (method == DeletionMethod.RecycleBin)
        {
            dialog.PrimaryButtonText = "Delete Permanently";
        }
        // Permanent run: no further action buttons — only Close.

        return dialog;
    }

    private static Grid BuildResultRow(
        string col0, string col1, string col2, bool isHeader)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, isHeader ? 0 : 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        double opacity = isHeader ? 0.55 : 1.0;

        var t0 = new TextBlock { Text = col0, Opacity = opacity };
        var t1 = new TextBlock { Text = col1, Opacity = opacity, HorizontalAlignment = HorizontalAlignment.Center };
        var t2 = new TextBlock { Text = col2, Opacity = opacity, HorizontalAlignment = HorizontalAlignment.Right };

        if (isHeader)
        {
            t0.FontSize = 11;
            t1.FontSize = 11;
            t2.FontSize = 11;
        }

        Grid.SetColumn(t0, 0);
        Grid.SetColumn(t1, 1);
        Grid.SetColumn(t2, 2);
        row.Children.Add(t0);
        row.Children.Add(t1);
        row.Children.Add(t2);
        return row;
    }
}
