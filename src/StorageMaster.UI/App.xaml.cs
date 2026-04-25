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

    private MainWindow? _window;

    public App()
    {
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
        services.AddSingleton<IScanRepository,      ScanRepository>();
        services.AddSingleton<ICleanupLogRepository, CleanupLogRepository>();
        services.AddSingleton<ISettingsRepository,   SettingsRepository>();

        // Platform
        services.AddSingleton<IDriveInfoProvider,     DriveInfoProvider>();
        services.AddSingleton<IFileDeleter,           FileDeleter>();
        services.AddSingleton<IRecycleBinInfoProvider, RecycleBinInfoProvider>();

        // Scanner
        services.AddSingleton<IFileScanner, FileScanner>();

        // Cleanup rules — registered in order of execution
        services.AddSingleton<ICleanupRule, RecycleBinCleanupRule>();
        services.AddSingleton<ICleanupRule, TempFilesCleanupRule>();
        services.AddSingleton<ICleanupRule, DownloadedInstallersRule>();
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
}
