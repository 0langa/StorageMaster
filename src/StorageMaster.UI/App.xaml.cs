using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

namespace StorageMaster.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StorageMaster",
        "logs",
        "startup-errors.log");

    /// <summary>Set to true when launched with --deep-scan (elevated restart).</summary>
    public static bool StartWithDeepScan { get; private set; }
    public static bool StartInTray { get; private set; }

    private MainWindow? _window;

    public App()
    {
        StartWithDeepScan = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--deep-scan", StringComparison.OrdinalIgnoreCase));
        StartInTray = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--start-in-tray", StringComparison.OrdinalIgnoreCase));

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Services ??= ServiceBootstrapper.BuildServices();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
        _ = ApplyRequestedThemeAsync();
        _ = ReconcileAbandonedScansAsync();
        _ = RunStartupUpdateCheckAsync();
    }

    /// <summary>
    /// Marks scan sessions abandoned by a previous process as interrupted. Without
    /// this they stay Running forever, look identical to a scan in progress, and
    /// their data is never reclaimable.
    /// </summary>
    private static async Task ReconcileAbandonedScansAsync()
    {
        try
        {
            var recovered = await Services
                .GetRequiredService<ScanSessionRecoveryService>()
                .ReconcileAsync()
                .ConfigureAwait(false);

            if (recovered > 0)
            {
                Services.GetRequiredService<ILogger<App>>().LogInformation(
                    "Marked {Count} abandoned scan session(s) as interrupted.", recovered);
            }
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Startup reconciliation of abandoned scans failed.");
        }
    }

    internal static void SetServices(IServiceProvider services) => Services = services;

    private static async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            var settings = await Services
                .GetRequiredService<ISettingsRepository>()
                .LoadAsync()
                .ConfigureAwait(false);

            if (!settings.CheckOnStartup)
                return;

            await Services
                .GetRequiredService<IUpdateService>()
                .CheckAsync(settings.IncludePrerelease)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // No startup path in the app currently cancels this task, but keep the
            // behavior explicit so cancellation never bubbles into the UI thread.
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>()
                .LogDebug(ex, "Silent startup update check failed");
        }
    }

    private async Task ApplyRequestedThemeAsync()
    {
        try
        {
            if (_window?.Content is not FrameworkElement root)
                return;

            var settings = await Services
                .GetRequiredService<ISettingsRepository>()
                .LoadAsync()
                .ConfigureAwait(true);

            root.RequestedTheme = settings.Theme switch
            {
                ThemePreference.Light => ElementTheme.Light,
                ThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>()
                .LogDebug(ex, "Failed to apply requested theme");
        }
    }

    private static void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        LogException("AppDomain.CurrentDomain.UnhandledException", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("TaskScheduler.UnobservedTaskException", e.Exception);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogException("Application.UnhandledException", e.Exception);
    }

    private static void LogException(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            var lines = new[]
            {
                $"[{DateTimeOffset.Now:O}] {source}",
                exception?.ToString() ?? "No exception details were provided.",
                string.Empty
            };
            File.AppendAllLines(CrashLogPath, lines);
        }
        catch
        {
            // Last-chance logging must never throw back into startup.
        }
    }
}
