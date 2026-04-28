using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Scanner;
using StorageMaster.Core.SmartCleaner;
using StorageMaster.Platform.Windows;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;
using StorageMaster.UI.Infrastructure;
using StorageMaster.UI.Pages;

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

    private MainWindow? _window;

    public App()
    {
        StartWithDeepScan = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--deep-scan", StringComparison.OrdinalIgnoreCase));

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Services = BuildServices();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug));

        // Infrastructure
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster", "storagemaster.db");
        services.AddSingleton(sp =>
            new StorageDbContext(dbPath, sp.GetRequiredService<ILogger<StorageDbContext>>()));

        // Repositories
        services.AddSingleton<IScanRepository,       ScanRepository>();
        services.AddSingleton<IScanErrorRepository,  ScanErrorRepository>();
        services.AddSingleton<ICleanupLogRepository, CleanupLogRepository>();
        services.AddSingleton<ISettingsRepository,   SettingsRepository>();

        // Platform
        services.AddSingleton<IDriveInfoProvider,         DriveInfoProvider>();
        services.AddSingleton<IFileDeleter,               FileDeleter>();
        services.AddSingleton<IRecycleBinInfoProvider,    RecycleBinInfoProvider>();
        services.AddSingleton<IAdminService,              AdminService>();
        services.AddSingleton<IInstalledProgramProvider,  InstalledProgramProvider>();

        // Managed scanner (primary / fallback)
        services.AddSingleton<FileScanner>(sp => new FileScanner(
            sp.GetRequiredService<IScanRepository>(),
            sp.GetRequiredService<ILogger<FileScanner>>(),
            sp.GetRequiredService<IScanErrorRepository>()));

        // Turbo Scanner (Rust-backed; falls back to FileScanner when binary absent)
        services.AddSingleton<TurboFileScanner>(sp => new TurboFileScanner(
            sp.GetRequiredService<IScanRepository>(),
            sp.GetRequiredService<ILogger<TurboFileScanner>>(),
            sp.GetRequiredService<FileScanner>(),
            sp.GetRequiredService<IScanErrorRepository>()));

        // IFileScanner resolved as managed scanner by default (ScanViewModel selects turbo at runtime)
        services.AddSingleton<IFileScanner>(sp => sp.GetRequiredService<FileScanner>());

        // Cleanup rules — registered in order of execution
        services.AddSingleton<ICleanupRule, RecycleBinCleanupRule>();
        services.AddSingleton<ICleanupRule, TempFilesCleanupRule>();
        services.AddSingleton<ICleanupRule>(sp => new DownloadedInstallersRule(
            sp.GetRequiredService<IScanRepository>(),
            KnownFolders.GetDownloadsPath));
        services.AddSingleton<ICleanupRule, CacheFolderCleanupRule>();
        services.AddSingleton<ICleanupRule, BrowserCacheCleanupRule>();
        services.AddSingleton<ICleanupRule, WindowsUpdateCacheRule>();
        services.AddSingleton<ICleanupRule, DeliveryOptimizationRule>();
        services.AddSingleton<ICleanupRule, WindowsErrorReportingRule>();
        services.AddSingleton<ICleanupRule>(sp => new UninstalledProgramLeftoversRule(
            sp.GetRequiredService<IInstalledProgramProvider>()));
        services.AddSingleton<ICleanupRule, LargeOldFilesCleanupRule>();

        services.AddSingleton<ICleanupEngine, CleanupEngine>();

        // Smart Cleaner
        services.AddSingleton<ISmartCleanerService, SmartCleanerService>();

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();

        // ViewModels
        services.AddTransient<DashboardViewModel>();
        services.AddSingleton<ScanViewModel>(sp => new ScanViewModel(
            sp.GetRequiredService<FileScanner>(),
            sp.GetRequiredService<TurboFileScanner>(),
            sp.GetRequiredService<IDriveInfoProvider>(),
            sp.GetRequiredService<INavigationService>(),
            sp.GetRequiredService<IAdminService>(),
            sp.GetRequiredService<ISettingsRepository>()));
        services.AddTransient<ResultsViewModel>();
        services.AddTransient<CleanupViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SmartCleanerViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
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
