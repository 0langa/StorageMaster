using System.Runtime.InteropServices;
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
        // Uses both Windows App SDK API and direct Win32 WM_SETICON so the icon is
        // reliable even when the Windows shell icon cache holds a stale entry.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "storagemaster.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
            SetWindowIconWin32(iconPath);
            RefreshShellIconCache();
        }

        // Navigate to dashboard on launch.
        _nav.NavigateTo(typeof(DashboardPage));
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    // ── Win32 icon helpers ────────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private void SetWindowIconWin32(string iconPath)
    {
        const uint  IMAGE_ICON      = 1;
        const uint  LR_LOADFROMFILE = 0x0010;
        const uint  WM_SETICON      = 0x0080;
        const nint  ICON_SMALL      = 0;
        const nint  ICON_BIG        = 1;

        var hwnd   = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var hSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        var hBig   = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);

        if (hSmall != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hSmall);
        if (hBig   != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG,   hBig);
    }

    /// <summary>Asks the Windows shell to invalidate its icon cache for this exe,
    /// so a pinned taskbar shortcut also picks up the new icon on next display.</summary>
    private static void RefreshShellIconCache()
    {
        try
        {
            const uint SHCNE_UPDATEITEM   = 0x00002000;
            const uint SHCNF_PATHW        = 0x0005;   // wide-char path
            const uint SHCNF_FLUSHNOWAIT  = 0x2000;

            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is null) return;

            var ptr = Marshal.StringToHGlobalUni(exePath);
            try   { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, ptr, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        catch { /* best-effort; never crash on startup for an icon */ }
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
