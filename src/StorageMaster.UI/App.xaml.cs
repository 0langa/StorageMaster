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

        // The language override is read by the resource system when a control is
        // created, so it has to be set before any content exists. Settings are read
        // synchronously here for that reason; the file is tiny and this runs once.
        ApplyStartupLanguage();

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Brush resources must exist before any page loads: an unresolved
        // StaticResource is a load-time failure, not a fallback.
        var theme = Services.GetRequiredService<Infrastructure.ThemeService>();
        theme.EnsureResources();

        // Seed the palette with its defaults before any window exists. The persisted
        // preference is applied moments later, but without this seed every palette
        // brush is transparent for the first frame — which is invisible on a surface
        // and very visible on text.
        theme.Apply(ThemePreference.Default, accentId: null);

        _window = Services.GetRequiredService<MainWindow>();
        if (_window.Content is FrameworkElement themeRoot)
        {
            theme.Attach(themeRoot);

            // Tag the tree with the chosen language so control templates resolve
            // their own text against it rather than the Windows display language.
            var tag = Infrastructure.LanguageService.TagFor(_startupLanguage);
            if (!string.IsNullOrEmpty(tag))
                themeRoot.Language = tag;
        }

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
            if (_window?.Content is not FrameworkElement)
                return;

            // ThemeService owns both the element theme and the palette, so they can
            // never drift apart.
            await Services
                .GetRequiredService<Infrastructure.ThemeService>()
                .ApplyFromSettingsAsync()
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>()
                .LogDebug(ex, "Failed to apply requested theme");
        }
    }

    /// <summary>
    /// Applies the persisted interface language before any UI is constructed.
    /// <para>
    /// Failure here is deliberately silent: an unreadable settings file must not
    /// stop the app from starting, and the fallback — letting Windows choose — is
    /// exactly what <see cref="UiLanguage.System"/> means anyway.
    /// </para>
    /// </summary>
    /// <summary>Language resolved at startup, reused when tagging the visual tree.</summary>
    private static UiLanguage _startupLanguage = UiLanguage.System;

    private static void ApplyStartupLanguage()
    {
        try
        {
            // Task.Run, then block. Blocking directly on LoadAsync deadlocks: the
            // repository hops to the thread pool and its continuation marshals back
            // to this thread, which is the one waiting. Running the whole chain
            // inside Task.Run gives it no SynchronizationContext to return to, so
            // the continuation completes on the pool and this wait always finishes.
            var settings = Task.Run(() =>
                    Services.GetRequiredService<ISettingsRepository>().LoadAsync())
                .GetAwaiter()
                .GetResult();

            _startupLanguage = settings.Language;
            Infrastructure.LanguageService.Apply(settings.Language);
        }
        catch (Exception)
        {
            Infrastructure.LanguageService.Apply(UiLanguage.System);
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
