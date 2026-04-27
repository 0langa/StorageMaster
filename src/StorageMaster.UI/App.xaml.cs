using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Scanner;
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
        // Check for --deep-scan before building services so ViewModels can read it.
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
        services.AddSingleton<IDriveInfoProvider,      DriveInfoProvider>();
        services.AddSingleton<IFileDeleter,            FileDeleter>();
        services.AddSingleton<IRecycleBinInfoProvider, RecycleBinInfoProvider>();
        services.AddSingleton<IAdminService,           AdminService>();

        // Scanner
        services.AddSingleton<IFileScanner>(sp => new FileScanner(
            sp.GetRequiredService<IScanRepository>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileScanner>>(),
            sp.GetRequiredService<IScanErrorRepository>()));

        // Cleanup rules — registered in order of execution
        services.AddSingleton<ICleanupRule, RecycleBinCleanupRule>();
        services.AddSingleton<ICleanupRule, TempFilesCleanupRule>();
        services.AddSingleton<ICleanupRule>(sp => new DownloadedInstallersRule(
            sp.GetRequiredService<IScanRepository>(),
            KnownFolders.GetDownloadsPath));
        services.AddSingleton<ICleanupRule, CacheFolderCleanupRule>();
        services.AddSingleton<ICleanupRule, LargeOldFilesCleanupRule>();
        services.AddSingleton<ICleanupEngine, CleanupEngine>();

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();

        // ViewModels
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ScanViewModel>();
        services.AddTransient<ResultsViewModel>();
        services.AddTransient<CleanupViewModel>();
        services.AddTransient<SettingsViewModel>();

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
