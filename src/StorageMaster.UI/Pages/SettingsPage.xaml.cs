using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StorageMaster.UI.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
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
}
