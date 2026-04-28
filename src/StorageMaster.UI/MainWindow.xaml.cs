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

        Title = "StorageMaster";
        ApplyStartupWindowSize();

        // Set window icon — covers title bar, taskbar button, and Alt+Tab thumbnail.
        // Path is relative to the exe; the .ico is copied to output by the csproj.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "storagemaster.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // Navigate to dashboard on launch.
        _nav.NavigateTo(typeof(DashboardPage));
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>
    /// Sizes and centres the window based on the actual work area of the display
    /// that will host it — respects DPI scaling, taskbar reservations, and
    /// multi-monitor setups.  Falls back gracefully if the API is unavailable.
    /// </summary>
    private void ApplyStartupWindowSize()
    {
        try
        {
            // DisplayArea.WorkArea is in *physical* (raw) pixels, which is what
            // AppWindow.Resize / Move also use — no manual DPI conversion needed.
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

            int w = (int)Math.Clamp(area.Width  * 0.85, 1200, 1800);
            int h = (int)Math.Clamp(area.Height * 0.85,  750, 1100);

            int x = area.X + (area.Width  - w) / 2;
            int y = area.Y + (area.Height - h) / 2;

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        }
        catch
        {
            // Fallback: fixed size if DisplayArea is somehow unavailable.
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        }
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
                "Dashboard"    => typeof(DashboardPage),
                "Scan"         => typeof(ScanPage),
                "Results"      => typeof(ResultsPage),
                "Cleanup"      => typeof(CleanupPage),
                "SmartCleaner" => typeof(SmartCleanerPage),
                _              => typeof(DashboardPage),
            };
            _nav.NavigateTo(page);
        }
    }
}
