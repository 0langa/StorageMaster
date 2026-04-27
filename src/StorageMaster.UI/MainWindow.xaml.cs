using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using StorageMaster.UI.Infrastructure;
using StorageMaster.UI.Pages;

namespace StorageMaster.UI;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _nav;

    public MainWindow(INavigationService nav)
    {
        _nav = nav;
        InitializeComponent();
        _nav.Initialize(ContentFrame);

        // Increase window size for a comfortable default work area.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        Title = "StorageMaster";

        // Set window icon — covers title bar, taskbar button, and Alt+Tab thumbnail.
        // Path is relative to the exe; the .ico is copied to output by the csproj.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "storagemaster.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Navigate to dashboard on launch.
        _nav.NavigateTo(typeof(DashboardPage));
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            _nav.NavigateTo(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            var page = item.Tag?.ToString() switch
            {
                "Dashboard" => typeof(DashboardPage),
                "Scan"      => typeof(ScanPage),
                "Results"   => typeof(ResultsPage),
                "Cleanup"   => typeof(CleanupPage),
                _           => typeof(DashboardPage),
            };
            _nav.NavigateTo(page);
        }
    }
}
