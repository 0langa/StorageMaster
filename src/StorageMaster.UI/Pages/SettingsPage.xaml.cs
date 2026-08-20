using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using StorageMaster.Core.Localization;
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

    /// <summary>
    /// Scrolls the open category editor to the bottom, for the capture harness.
    /// <para>
    /// A validation message usually sits below the fold of a long category. Capturing
    /// the editor as it opens shows a disabled Save button and none of the text that
    /// explains why, which is the half that needs reviewing in each language.
    /// </para>
    /// </summary>
    internal void CaptureScrollEditorToEnd()
    {
        EditorScrollViewer.UpdateLayout();
        EditorScrollViewer.ChangeView(null, EditorScrollViewer.ScrollableHeight, null, disableAnimation: true);
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
            // The user-facing sentence is localized; the exception detail appended to
            // it stays English, the same way the logs read it.
            ViewModel.SavedMessage = Loc.Format("Settings_FolderPicker_Error", ex.Message);
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
                    ? Loc.Format("Settings_Schedule_Weekly", job.WeeklyDay, job.StartTimeLocal)
                    : Loc.Format("Settings_Schedule_Daily", job.StartTimeLocal);
                var confirmation = new ContentDialog
                {
                    Title = Loc.Get("Safety_Settings_ScheduledCleanupDialog_Title"),
                    Content = Loc.Format(
                        "Safety_Settings_ScheduledCleanupDialog_Body",
                        job.Name,
                        job.TargetPath,
                        rules,
                        schedule),
                    PrimaryButtonText = Loc.Get("Safety_Settings_ScheduledCleanupDialog_Confirm"),
                    CloseButtonText = Loc.Get("Common_Cancel"),
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
