using System.Runtime.InteropServices;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using StorageMaster.UI.Infrastructure;
using StorageMaster.Core.Interfaces;
using StorageMaster.UI.Pages;
using Windows.Graphics;

namespace StorageMaster.UI;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _nav;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IScanRepository _scanRepository;
    private readonly IDriveInfoProvider _driveInfoProvider;
    private readonly IDriveHealthProvider _driveHealthProvider;
    private readonly DesktopNotificationService _notificationService;
    private readonly ILocalDiagnosticsService _diagnostics;
    private readonly TaskbarIcon _trayIcon;
    private bool _isUpdatingSelection;
    private bool _allowClose;
    private DateTimeOffset? _notificationsPausedUntilUtc;
    private DispatcherQueueTimer? _diskMonitorTimer;

    public MainWindow(
        INavigationService nav,
        ISettingsRepository settingsRepository,
        IScanRepository scanRepository,
        IDriveInfoProvider driveInfoProvider,
        IDriveHealthProvider driveHealthProvider,
        DesktopNotificationService notificationService,
        ILocalDiagnosticsService diagnostics)
    {
        _nav = nav;
        _settingsRepository = settingsRepository;
        _scanRepository = scanRepository;
        _driveInfoProvider = driveInfoProvider;
        _driveHealthProvider = driveHealthProvider;
        _notificationService = notificationService;
        _diagnostics = diagnostics;
        _trayIcon = CreateTrayIcon();
        InitializeComponent();
        _nav.Initialize(ContentFrame);
        _nav.Navigated += OnNavigated;
        _notificationService.NotificationRaised += OnNotificationRaised;
        NavView.DisplayModeChanged += (_, _) => UpdatePaneFooterVisibility();
        NavView.PaneOpened += (_, _) => UpdatePaneFooterVisibility();
        NavView.PaneClosed += (_, _) => UpdatePaneFooterVisibility();

        Title = "StorageMaster";
        TryApplyMicaBackdrop();
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
        SyncSelection(typeof(DashboardPage));
        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;
    }

    private void TryApplyMicaBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        catch
        {
            SystemBackdrop = null;
        }
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
        const uint IMAGE_ICON = 1;
        const uint LR_LOADFROMFILE = 0x0010;
        const uint WM_SETICON = 0x0080;
        const nint ICON_SMALL = 0;
        const nint ICON_BIG = 1;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var hSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        var hBig = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);

        if (hSmall != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hSmall);
        if (hBig != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hBig);
    }

    /// <summary>Asks the Windows shell to invalidate its icon cache for this exe,
    /// so a pinned taskbar shortcut also picks up the new icon on next display.</summary>
    private static void RefreshShellIconCache()
    {
        try
        {
            const uint SHCNE_UPDATEITEM = 0x00002000;
            const uint SHCNF_PATHW = 0x0005;   // wide-char path
            const uint SHCNF_FLUSHNOWAIT = 0x2000;

            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath is null) return;

            var ptr = Marshal.StringToHGlobalUni(exePath);
            try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, ptr, IntPtr.Zero); }
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

            int w = (int)Math.Clamp(area.Width * 0.85, 1200, 1800);
            int h = (int)Math.Clamp(area.Height * 0.85, 750, 1100);

            int x = area.X + (area.Width - w) / 2;
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
        if (_isUpdatingSelection)
            return;

        if (args.IsSettingsSelected)
        {
            _nav.NavigateTo(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            if (tag is not null && NavigationRoutes.TagToPage.TryGetValue(tag, out var page))
                _nav.NavigateTo(page);
        }
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (_nav.CanGoBack)
            _nav.GoBack();
    }

    private void OnNavigated(object? sender, NavigationChangedEventArgs e)
    {
        SyncSelection(e.PageType);
        NavView.IsBackEnabled = _nav.CanGoBack;
    }

    private void SyncSelection(Type? pageType)
    {
        _isUpdatingSelection = true;
        try
        {
            if (pageType == typeof(SettingsPage))
            {
                NavView.SelectedItem = NavView.SettingsItem;
                return;
            }

            var tag = pageType is not null && NavigationRoutes.PageToTag.TryGetValue(pageType, out var routeTag)
                ? routeTag
                : NavigationRoutes.Dashboard;

            NavView.SelectedItem = NavView.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    /// <summary>
    /// Hides the PaneFooter status block when the pane is collapsed/compact so
    /// the text does not render character-by-character in the 56 px icon strip.
    /// </summary>
    private void UpdatePaneFooterVisibility()
    {
        PaneFooterBorder.Visibility =
            NavView.DisplayMode == NavigationViewDisplayMode.Expanded || NavView.IsPaneOpen
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnWindowActivated;
        try
        {
            var settings = await _settingsRepository.LoadAsync();
            ConfigureDiskMonitor(settings);
            await RefreshGlobalStatusAsync();
            // MinimizeToTray only changes close-button behavior; a normal launch
            // must stay visible. Only the explicit --start-in-tray flag hides here.
            if (App.StartInTray)
                HideToTray();
        }
        catch (Exception ex)
        {
            // Keep startup resilient even if tray bootstrap fails.
            await RecordDiagnosticsAsync("startup-tray", ex);
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        try
        {
            var settings = await _settingsRepository.LoadAsync();
            if (!settings.MinimizeToTray)
                return;

            args.Cancel = true;
            HideToTray();
        }
        catch (Exception ex)
        {
            await RecordDiagnosticsAsync("window-closing", ex);
        }
    }

    private void HideToTray()
    {
        _trayIcon.ForceCreate(false);
        WindowExtensions.Hide(this, enableEfficiencyMode: false);
    }

    private void ShowFromTray()
    {
        WindowExtensions.Show(this, false);
        Activate();
    }

    private void ConfigureDiskMonitor(Core.Models.AppSettings settings)
    {
        _diskMonitorTimer?.Stop();
        if (!settings.EnableLowDiskNotifications && !settings.EnableDriveHealthNotifications)
            return;

        _diskMonitorTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _diskMonitorTimer.Interval = TimeSpan.FromMinutes(15);
        _diskMonitorTimer.Tick += DiskMonitorTimer_Tick;
        _diskMonitorTimer.Start();
        _ = CheckLowDiskAsync();
    }

    private async void DiskMonitorTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            await CheckLowDiskAsync();
        }
        catch (Exception ex)
        {
            await RecordDiagnosticsAsync("disk-monitor", ex);
        }
    }

    private async Task CheckLowDiskAsync()
    {
        try
        {
            var settings = await _settingsRepository.LoadAsync();
            if (!settings.EnableLowDiskNotifications && !settings.EnableDriveHealthNotifications)
                return;

            if (_notificationsPausedUntilUtc is { } pausedUntil && pausedUntil > DateTimeOffset.UtcNow)
                return;

            if (settings.EnableLowDiskNotifications)
            {
                var state = settings.LowDiskNotificationState;
                var lowestFreePercent = 100;
                foreach (var drive in _driveInfoProvider.GetAvailableDrives())
                {
                    if (!drive.IsReady || drive.TotalBytes <= 0)
                        continue;

                    var freePercent = (int)Math.Round((double)drive.FreeBytes / drive.TotalBytes * 100.0);
                    lowestFreePercent = Math.Min(lowestFreePercent, freePercent);
                    string? level = freePercent <= settings.LowDiskCriticalPercent
                        ? "critical"
                        : freePercent <= settings.LowDiskWarningPercent
                            ? "warning"
                            : null;
                    if (level is null)
                        continue;

                    var stateKey = $"{drive.Name}|{level}";
                    if (state.TryGetValue(stateKey, out var lastText) &&
                        DateTimeOffset.TryParse(lastText, out var lastUtc) &&
                        lastUtc > DateTimeOffset.UtcNow.AddHours(-12))
                    {
                        continue;
                    }

                    var stamp = DateTimeOffset.UtcNow.ToString("O");
                    state[stateKey] = stamp;
                    // Targeted mutation: a concurrent settings-page save must not
                    // be clobbered by this monitor's whole-snapshot write.
                    await _settingsRepository.UpdateAsync(s => s.LowDiskNotificationState[stateKey] = stamp);
                    await _notificationService.ShowWarningAsync(
                        "Low disk space",
                        $"{drive.Name} is down to {freePercent}% free ({drive.FreeBytes / 1024 / 1024 / 1024.0:F1} GB).");
                }

                PaneDiskStatusText.Text = lowestFreePercent == 100
                    ? "Drives healthy"
                    : $"Lowest free space: {lowestFreePercent}%";
                WarningStatusText.Text = PaneDiskStatusText.Text;
            }

            if (!settings.EnableDriveHealthNotifications)
                return;

            foreach (var snapshot in await _driveHealthProvider.GetHealthAsync())
            {
                if (snapshot.Status is not (Core.Models.DriveHealthStatus.Warning or Core.Models.DriveHealthStatus.Critical))
                    continue;

                var stateKey = $"{snapshot.DriveName}|{snapshot.Status}";
                if (settings.DriveHealthNotificationState.TryGetValue(stateKey, out var lastText) &&
                    DateTimeOffset.TryParse(lastText, out var lastUtc) &&
                    lastUtc > DateTimeOffset.UtcNow.AddHours(-12))
                {
                    continue;
                }

                var healthStamp = DateTimeOffset.UtcNow.ToString("O");
                settings.DriveHealthNotificationState[stateKey] = healthStamp;
                await _settingsRepository.UpdateAsync(s => s.DriveHealthNotificationState[stateKey] = healthStamp);
                await _notificationService.ShowWarningAsync(
                    "Drive health needs attention",
                    $"{snapshot.DriveName}: {snapshot.Message}");
            }
        }
        catch (Exception ex)
        {
            await RecordDiagnosticsAsync("disk-monitor", ex);
        }
    }

    private async Task RefreshGlobalStatusAsync()
    {
        try
        {
            var latest = (await _scanRepository.GetRecentSessionsAsync(1)).FirstOrDefault();
            if (latest is null)
            {
                LatestScanStatusText.Text = "No scans yet";
                PaneLatestScanText.Text = "Start a scan to build workspace data";
                return;
            }

            var completed = latest.CompletedUtc?.ToLocalTime().ToString("g") ?? "in progress";
            LatestScanStatusText.Text = $"Last scan: {latest.RootPath}";
            PaneLatestScanText.Text = $"{latest.Status} · {completed}";
        }
        catch (Exception ex)
        {
            await RecordDiagnosticsAsync("global-status", ex);
        }
    }

    private void OnNotificationRaised(object? sender, DesktopNotificationEventArgs e)
    {
        _trayIcon.ForceCreate(false);
        var icon = e.Level switch
        {
            "error" => NotificationIcon.Error,
            "warning" => NotificationIcon.Warning,
            _ => NotificationIcon.Info,
        };
        _trayIcon.ShowNotification(e.Title, e.Message, icon);
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowFromTray();

    private void TraySmartClean_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        _nav.NavigateTo(typeof(SmartCleanerPage));
    }

    private void TrayStartScan_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        _nav.NavigateTo(typeof(ScanPage));
    }

    private void TrayReviewDuplicates_Click(object sender, RoutedEventArgs e)
    {
        ShowFromTray();
        _nav.NavigateTo(typeof(DuplicatesPage));
    }

    private void TrayPauseNotifications_Click(object sender, RoutedEventArgs e)
    {
        _notificationsPausedUntilUtc = DateTimeOffset.UtcNow.AddHours(12);
        _trayIcon.ShowNotification("Notifications paused", "Low-disk tray notifications are paused for 12 hours.", NotificationIcon.Info);
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        _trayIcon.Dispose();
        Close();
    }

    private TaskbarIcon CreateTrayIcon()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MakeMenuItem("Open StorageMaster", TrayOpen_Click));
        menu.Items.Add(MakeMenuItem("Run Smart Clean", TraySmartClean_Click));
        menu.Items.Add(MakeMenuItem("Start Scan", TrayStartScan_Click));
        menu.Items.Add(MakeMenuItem("Review Duplicates", TrayReviewDuplicates_Click));
        menu.Items.Add(MakeMenuItem("Pause Notifications", TrayPauseNotifications_Click));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeMenuItem("Exit", TrayExit_Click));

        var trayIcon = new TaskbarIcon
        {
            ToolTipText = "StorageMaster",
            ContextFlyout = menu,
            MenuActivation = PopupActivationMode.LeftOrRightClick,
        };

        // Prefer the shipped application icon; the generated "SM" glyph is only
        // a fallback for builds where the asset is missing.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "storagemaster.ico");
        if (File.Exists(iconPath))
            trayIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
        else
            trayIcon.IconSource = new GeneratedIconSource { Text = "SM" };

        return trayIcon;
    }

    private static MenuFlyoutItem MakeMenuItem(string text, RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
        };
        item.Click += handler;
        return item;
    }

    private async Task RecordDiagnosticsAsync(string category, Exception ex)
    {
        try
        {
            await _diagnostics.RecordAsync(category, ex.ToString());
        }
        catch
        {
            // Diagnostics must never become a secondary startup/shutdown failure.
        }
    }
}
