using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;
using StorageMaster.Core.SmartCleaner;
using StorageMaster.Core.Update;
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
        _ = ApplyRequestedThemeAsync();
        _ = RunStartupUpdateCheckAsync();
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
        services.AddSingleton<DuplicateRepository>();
        services.AddSingleton<IDuplicateRepository>(sp => sp.GetRequiredService<DuplicateRepository>());
        services.AddSingleton<IDuplicateCandidateProvider>(sp => sp.GetRequiredService<DuplicateRepository>());

        services.AddSingleton(_ =>
            Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0));

        services.AddSingleton(sp =>
        {
            var currentVersion = sp.GetRequiredService<Version>().ToString(3);
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("StorageMaster", currentVersion));
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        });
        services.AddSingleton<IUpdateService>(sp => new GitHubUpdateService(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<Version>(),
            sp.GetRequiredService<ILogger<GitHubUpdateService>>()));

        // Platform
        services.AddSingleton<IDriveInfoProvider,         DriveInfoProvider>();
        services.AddSingleton<IFileDeleter,               FileDeleter>();
        services.AddSingleton<IRecycleBinInfoProvider,    RecycleBinInfoProvider>();
        services.AddSingleton<IAdminService,              AdminService>();
        services.AddSingleton<IInstalledProgramProvider,  InstalledProgramProvider>();
        services.AddSingleton<IFileIdentityProvider, FileIdentityProvider>();

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
        services.AddSingleton<ICleanupRule, ThumbnailCacheRule>();
        services.AddSingleton<ICleanupRule, IconCacheRule>();
        services.AddSingleton<ICleanupRule, FontCacheRule>();
        services.AddSingleton<ICleanupRule, DnsClientCacheRule>();
        services.AddSingleton<ICleanupRule>(sp => new PrefetchFilesRule(
            sp.GetRequiredService<IAdminService>()));
        services.AddSingleton<ICleanupRule, MicrosoftStoreLogsRule>();
        services.AddSingleton<ICleanupRule, DuplicateFilesCleanupRule>();

        services.AddSingleton<ICleanupEngine, CleanupEngine>();
        services.AddSingleton<IScanResultDeletionService, ScanResultDeletionService>();

        // Smart Cleaner
        services.AddSingleton<ISmartCleanerService, SmartCleanerService>();
        services.AddSingleton<IFileContentHasher, FileContentHasher>();
        services.AddSingleton<IFileSnapshotProvider, FileSnapshotProvider>();

        // Duplicate detection strategies
        services.AddSingleton<IDuplicateDetectionStrategy>(sp =>
            new ExactSha256Strategy(
                sp.GetRequiredService<IFileContentHasher>(),
                sp.GetRequiredService<IFileSnapshotProvider>()));
        services.AddSingleton<IDuplicateDetectionStrategy>(sp =>
            new NormalizedTextStrategy(
                sp.GetRequiredService<IFileSnapshotProvider>()));
        services.AddSingleton<IDuplicateDetectionStrategy>(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsRepository>()
                .LoadAsync().GetAwaiter().GetResult();
            return new ImagePHashStrategy(
                sp.GetRequiredService<IFileSnapshotProvider>(),
                settings.DuplicateImagePHashThreshold);
        });
        // VideoPHashStrategy: always registered; IsAvailable=false when ffmpeg absent.
        // DuplicateFinderService validates availability before running any phase.
        services.AddSingleton<IDuplicateDetectionStrategy>(sp =>
        {
            var settings  = sp.GetRequiredService<ISettingsRepository>()
                              .LoadAsync().GetAwaiter().GetResult();
            var ffmpegExe = string.IsNullOrWhiteSpace(settings.FfmpegPath)
                ? string.Empty
                : Path.Combine(settings.FfmpegPath, "ffmpeg.exe");
            return new VideoPHashStrategy(
                ffmpegExe,
                sp.GetRequiredService<IFileSnapshotProvider>(),
                settings.DuplicateMaxVideoDurationSeconds);
        });

        services.AddSingleton<IDuplicateKeeperPolicy, DuplicateKeeperPolicy>();
        services.AddSingleton<IDuplicateFinderService>(sp =>
            new DuplicateFinderService(
                sp.GetRequiredService<IDuplicateRepository>(),
                sp.GetRequiredService<IDuplicateCandidateProvider>(),
                sp.GetRequiredService<IFileContentHasher>(),
                sp.GetServices<IDuplicateDetectionStrategy>(),
                sp.GetRequiredService<IDuplicateKeeperPolicy>(),
                sp.GetRequiredService<ILogger<DuplicateFinderService>>()));
        services.AddSingleton<IDuplicateDeletionService, DuplicateDeletionService>();

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();

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
        services.AddTransient<DuplicatesViewModel>();
        services.AddTransient<CleanupViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SmartCleanerViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

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
