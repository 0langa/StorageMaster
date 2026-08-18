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
    private CancellationTokenSource? _navigationCts;
    private int _navigationGeneration;
    private bool _isPageActive;

    public CleanupPage()
    {
        ViewModel = App.Services.GetRequiredService<CleanupViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isPageActive = true;
        var navigationGeneration = Interlocked.Increment(ref _navigationGeneration);
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        var navigationCts = new CancellationTokenSource();
        _navigationCts = navigationCts;
        var requestedSessionId = e.Parameter is long sessionId ? sessionId : (long?)null;
        try
        {
            await ViewModel.InitializeAsync(requestedSessionId, navigationCts.Token);
        }
        catch (OperationCanceledException) when (navigationCts.IsCancellationRequested)
        {
            // Expected when navigation supersedes initialization.
        }
        catch (Exception ex)
        {
            if (IsCurrentNavigation(navigationGeneration))
                ViewModel.StatusMessage = $"Cleanup initialization failed: {ex.Message}";
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _isPageActive = false;
        Interlocked.Increment(ref _navigationGeneration);
        ViewModel.CancelPendingAnalysis();
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
        base.OnNavigatedFrom(e);
    }

    // ── Group expand/collapse ──────────────────────────────────────────────

    private void GroupChevron_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CleanupCategoryGroup group)
            group.IsExpanded = !group.IsExpanded;
    }

    // ── "Clean Up Selected…" button ────────────────────────────────────────

    private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        var navigationGeneration = Volatile.Read(ref _navigationGeneration);
        if (!IsCurrentNavigation(navigationGeneration))
            return;

        try
        {
            ViewModel.UpdateTotalSelected();

            var isDryRun = ViewModel.IsDryRun;
            var size = ViewModel.TotalSelectedSize;

            string title = isDryRun ? "Confirm Dry Run Preview" : "Confirm Cleanup";
            string message = isDryRun
                ? $"This will simulate the cleanup for {size} of selected items without deleting anything. Continue?"
                : ViewModel.UseRecycleBin
                    ? $"This will send {size} of selected files and folders to the Recycle Bin (recoverable). " +
                      "Disk space remains in use until the Recycle Bin is emptied. Continue?"
                    : $"This will PERMANENTLY delete {size} of selected files and folders. This cannot be undone. " +
                      "Displayed size is logical file size; actual reclaimed disk allocation can differ. Continue?";

            var confirm = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = isDryRun ? "Run Preview" : "Clean Up",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };

            if (await confirm.ShowAsync() != ContentDialogResult.Primary ||
                !IsCurrentNavigation(navigationGeneration))
            {
                return;
            }

            await ViewModel.ExecuteCleanupCommand.ExecuteAsync(null);
            if (!IsCurrentNavigation(navigationGeneration))
                return;

            await ShowReportLoopAsync(navigationGeneration);
        }
        catch (Exception ex)
        {
            if (IsCurrentNavigation(navigationGeneration))
                ViewModel.StatusMessage = $"Cleanup UI could not show a confirmed result: {ex.Message}";
        }
    }

    // ── Report dialog loop ─────────────────────────────────────────────────

    /// <summary>
    /// Shows the cleanup report. If the user chooses to re-run with a different
    /// deletion mode (e.g. after a dry run) the loop runs the engine again and
    /// shows a fresh report — at most three passes (dry → recycle → permanent).
    /// </summary>
    private async Task ShowReportLoopAsync(int navigationGeneration)
    {
        while (true)
        {
            if (!IsCurrentNavigation(navigationGeneration))
                return;

            bool wasDry = ViewModel.LastRunWasDryRun;
            DeletionMethod wasMethod = ViewModel.LastRunDeletionMethod;
            var results = ViewModel.ExecutionResults.ToList();
            string summary = ViewModel.LastRunSummary;

            var dialog = BuildReportDialog(
                wasDry,
                wasMethod,
                results,
                summary,
                ViewModel.LastPreviewAllowsExecution);
            var choice = await dialog.ShowAsync();
            if (!IsCurrentNavigation(navigationGeneration))
                return;

            if (choice == ContentDialogResult.Primary &&
                wasDry &&
                ViewModel.LastPreviewAllowsExecution)
            {
                // "Delete (Recycle Bin)" — first real run, use RecycleBin
                if (!await ViewModel.RunCleanupWithMethodAsync(
                        dryRun: false,
                        DeletionMethod.RecycleBin))
                {
                    break;
                }
            }
            else if (choice == ContentDialogResult.Secondary &&
                     wasDry &&
                     ViewModel.LastPreviewAllowsExecution)
            {
                // "Delete Permanently" — skip recycle bin altogether
                if (!await ConfirmPermanentDeletionAfterPreviewAsync(navigationGeneration))
                    break;
                if (!await ViewModel.RunCleanupWithMethodAsync(
                        dryRun: false,
                        DeletionMethod.Permanent))
                {
                    break;
                }
            }
            else
            {
                break; // User dismissed, or no further action available.
            }
        }
    }

    // ── Dialog builder ─────────────────────────────────────────────────────

    private ContentDialog BuildReportDialog(
        bool isDryRun,
        DeletionMethod method,
        IReadOnlyList<CleanupResultDisplay> results,
        string summary,
        bool previewAllowsExecution)
    {
        // ── Content ────────────────────────────────────────────────────────

        var mainStack = new StackPanel { Spacing = 16 };

        // Summary row
        if (!string.IsNullOrWhiteSpace(summary))
        {
            mainStack.Children.Add(new TextBlock
            {
                Text = summary,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.85,
            });
        }

        // Header row
        if (results.Count > 0)
        {
            var amountHeading = isDryRun
                ? "Est. size"
                : method == DeletionMethod.RecycleBin
                    ? "Moved"
                    : "Deleted";
            var header = BuildResultRow("Item", "Status", amountHeading, isHeader: true);
            mainStack.Children.Add(header);

            var divider = new Border
            {
                Height = 1,
                Opacity = 0.2,
                Background = new SolidColorBrush(Colors.Gray),
                Margin = new Thickness(0, 0, 0, 4),
            };
            mainStack.Children.Add(divider);
        }

        // Per-item rows
        foreach (var r in results)
        {
            bool ok = r.Status is "Success";
            bool partial = r.Status is "PartialSuccess";
            bool skipped = r.Status is "Skipped";

            var titleText = new TextBlock
            {
                Text = r.Title,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var titleCell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            titleCell.Children.Add(titleText);

            if (!string.IsNullOrWhiteSpace(r.Error))
            {
                titleCell.Children.Add(new TextBlock
                {
                    Text = r.Error,
                    FontSize = 11,
                    Opacity = 0.7,
                    Foreground = new SolidColorBrush(Colors.OrangeRed),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            var statusText = new TextBlock
            {
                Text = r.Status,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Opacity = ok || partial ? 0.9 : 0.6,
                Foreground = ok
                    ? new SolidColorBrush(Colors.MediumSeaGreen)
                    : partial
                        ? new SolidColorBrush(Colors.DarkOrange)
                    : skipped
                        ? new SolidColorBrush(Colors.Gray)
                        : new SolidColorBrush(Colors.OrangeRed),
            };

            var sizeText = new TextBlock
            {
                Text = r.WasDryRun ? $"~{r.BytesFreed}" : r.BytesFreed,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
            };

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            Grid.SetColumn(titleCell, 0);
            Grid.SetColumn(statusText, 1);
            Grid.SetColumn(sizeText, 2);
            row.Children.Add(titleCell);
            row.Children.Add(statusText);
            row.Children.Add(sizeText);
            mainStack.Children.Add(row);
        }

        var scrollContent = new ScrollViewer
        {
            MaxHeight = 380,
            Content = mainStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        // ── Dialog ─────────────────────────────────────────────────────────

        var hasPartial = results.Any(result => result.Status == "PartialSuccess");
        var hasFailure = results.Any(result => result.Status is "Failed" or "Skipped");
        string title = isDryRun
            ? results.Count == 0
                ? "Dry Run Report — Preview failed"
                : previewAllowsExecution
                    ? "Dry Run Report — Ready for deletion review"
                    : "Dry Run Report — Preview incomplete"
            : results.Count == 0
                ? "Cleanup Report — No confirmed outcome"
                : hasPartial || hasFailure
                ? "Cleanup Report — Partial or failed outcome"
                : method == DeletionMethod.RecycleBin
                    ? "Cleanup Report — Recycle Bin move complete"
                    : "Cleanup Report — Permanent deletion complete";

        var dialog = new ContentDialog
        {
            Title = title,
            Content = scrollContent,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close,
        };

        // Add action buttons depending on what the last run was. After a real
        // run the files are no longer at their original paths, so a follow-up
        // "Delete Permanently" pass would only report meaningless successes —
        // real runs get Close only.
        if (isDryRun && previewAllowsExecution)
        {
            dialog.PrimaryButtonText = "Delete (Recycle Bin)";
            dialog.SecondaryButtonText = "Delete Permanently";
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        return dialog;
    }

    private async Task<bool> ConfirmPermanentDeletionAfterPreviewAsync(
        int navigationGeneration)
    {
        if (!IsCurrentNavigation(navigationGeneration))
            return false;

        var confirm = new ContentDialog
        {
            Title = "Confirm Permanent Deletion",
            Content =
                $"This will permanently delete the exact {ViewModel.LastRunSelectedSizeDisplay} selection from the successful preview. " +
                "This cannot be undone. Files will be revalidated and changed paths will fail closed. Continue?",
            PrimaryButtonText = "Delete Permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        var result = await confirm.ShowAsync();
        return IsCurrentNavigation(navigationGeneration) &&
               result == ContentDialogResult.Primary;
    }

    private bool IsCurrentNavigation(int generation) =>
        _isPageActive &&
        generation == Volatile.Read(ref _navigationGeneration) &&
        XamlRoot is not null;

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
