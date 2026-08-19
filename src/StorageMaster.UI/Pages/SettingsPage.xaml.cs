using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scheduling;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StorageMaster.UI.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private CancellationTokenSource? _navigationCts;

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshEditorTemplate();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        var navigationCts = new CancellationTokenSource();
        _navigationCts = navigationCts;
        try
        {
            await ViewModel.LoadAsync(navigationCts.Token);
            await ViewModel.RefreshDatabaseSizeAsync();
        }
        catch (OperationCanceledException) when (navigationCts.IsCancellationRequested)
        {
            // Expected when navigation supersedes settings initialization.
        }
        catch (Exception ex)
        {
            ViewModel.ReportLoadFailure(ex);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
        base.OnNavigatedFrom(e);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Windows.System.VirtualKey.Escape && ViewModel.IsEditorOpen)
        {
            ViewModel.CancelCategoryCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedCategory)
                           or nameof(SettingsViewModel.IsEditorOpen))
            RefreshEditorTemplate();
    }

    private void RefreshEditorTemplate()
    {
        var selector = (SettingsCategoryTemplateSelector)Resources["CategorySelector"];
        EditorContentControl.ContentTemplate = selector.SelectTemplate(ViewModel, EditorContentControl);
    }

    private void CategoriesGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SettingsCategoryItem item)
            ViewModel.OpenCategoryCommand.Execute(item.Category);
    }

    private void RemovePath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
            ViewModel.RemoveExcludedPathEntry(path);
    }

    private async void AddExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            var hwnd = WindowNative.GetWindowHandle(App.Services.GetRequiredService<MainWindow>());
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                ViewModel.AddExcludedPath(folder.Path);
        }
        catch (Exception ex)
        {
            ViewModel.SavedMessage = $"Could not open folder picker: {ex.Message}";
        }
    }

    private async void SaveScheduledJob_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ViewModel.TryCreateScheduledJobDraft(out var job))
                return;

            if (job.Kind == ScheduledJobKind.CleanupExecuteSafe && job.Enabled)
            {
                var rules = string.Join(", ", ScheduledCleanupPolicy.GetEffectiveRules(job.RulesCsv));
                var schedule = job.Frequency == ScheduledJobFrequency.Weekly
                    ? $"Weekly on {job.WeeklyDay} at {job.StartTimeLocal}"
                    : $"Daily at {job.StartTimeLocal}";
                var confirmation = new ContentDialog
                {
                    Title = "Enable Unattended Cleanup?",
                    Content =
                        $"Job: {job.Name}\n" +
                        $"Target: {job.TargetPath}\n" +
                        $"Rules: {rules}\n" +
                        $"Schedule: {schedule}\n" +
                        "Deletion method: Recycle Bin\n" +
                        "Allowed risk: Safe or Low only\n" +
                        "Clear entire Downloads folder: Disabled\n\n" +
                        "StorageMaster will run this exact cleanup plan automatically. " +
                        "Any later plan change invalidates consent and blocks execution until you review and save it again. " +
                        "Disk allocation remains used until the Recycle Bin is emptied.",
                    PrimaryButtonText = "Enable Scheduled Cleanup",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };

                if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
                    return;

                job = ScheduledCleanupPolicy.GrantCurrentConsent(job);
            }

            await ViewModel.SaveScheduledJobAsync(job);
        }
        catch (Exception ex)
        {
            ViewModel.ReportScheduledJobUiFailure(ex);
        }
    }
}
