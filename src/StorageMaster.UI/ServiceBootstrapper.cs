using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

internal static class ServiceBootstrapper
{
    /// <summary>
    /// Directory holding the database, logs and quarantine.
    /// <para>
    /// Defaults to <c>%LOCALAPPDATA%\StorageMaster</c>. Setting
    /// <c>STORAGEMASTER_DATA_DIR</c> redirects everything to another folder, which
    /// is what a development or test build should use.
    /// </para>
    /// <para>
    /// This exists because schema migrations are one-way. Running an in-progress
    /// build against the real database upgrades it past what the released version
    /// understands, and the released version then refuses to open it — correctly,
    /// but the user is left with an app that cannot start. An override makes the
    /// safe choice available instead of relying on remembering to be careful.
    /// </para>
    /// </summary>
    public static string ResolveDataDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("STORAGEMASTER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
            return Path.GetFullPath(overridden.Trim());

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster");
    }

    public static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Debug));

        var dbPath = Path.Combine(ResolveDataDirectory(), "storagemaster.db");
        services.AddSingleton(sp =>
            new StorageDbContext(dbPath, sp.GetRequiredService<ILogger<StorageDbContext>>()));

        services.AddSingleton<IScanRepository, ScanRepository>();
        services.AddSingleton<ISpaceMapRepository, SpaceMapRepository>();
        services.AddSingleton<IScanErrorRepository, ScanErrorRepository>();
        services.AddSingleton<ICleanupLogRepository, CleanupLogRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<ISettingsRepository>(sp => sp.GetRequiredService<SettingsRepository>());
        services.AddSingleton<ISettingsSnapshotProvider>(sp => sp.GetRequiredService<SettingsRepository>());
        services.AddSingleton<DuplicateRepository>();
        services.AddSingleton<IDuplicateRepository>(sp => sp.GetRequiredService<DuplicateRepository>());
        services.AddSingleton<IDuplicateCandidateProvider>(sp => sp.GetRequiredService<DuplicateRepository>());
        services.AddSingleton<IQuarantineRecorder>(sp => sp.GetRequiredService<DuplicateRepository>());
        services.AddSingleton<IDriveHealthRepository, DriveHealthRepository>();

        services.AddSingleton(_ =>
            Assembly.GetEntryAssembly()?.GetName().Version
            ?? Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0));
        services.AddSingleton(_ => GetCurrentVersionText());

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
        services.AddSingleton<IInstallerTrustVerifier, InstallerTrustVerifier>();
        services.AddSingleton<IUpdateService>(sp => new GitHubUpdateService(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<string>(),
            sp.GetRequiredService<ILogger<GitHubUpdateService>>(),
            sp.GetRequiredService<ISettingsRepository>(),
            sp.GetRequiredService<IInstallerTrustVerifier>()));

        services.AddSingleton<IDriveInfoProvider, DriveInfoProvider>();
        services.AddSingleton<IDriveHealthProvider, DriveHealthProvider>();
        services.AddSingleton<IFileDeleter, FileDeleter>();
        services.AddSingleton<IRecycleBinInfoProvider, RecycleBinInfoProvider>();
        services.AddSingleton<IAdminService, AdminService>();
        services.AddSingleton<IInstalledProgramProvider, InstalledProgramProvider>();
        services.AddSingleton<IFileIdentityProvider, FileIdentityProvider>();
        services.AddSingleton<IDirectoryFileIdentityProvider, DirectoryFileIdentityProvider>();
        services.AddSingleton<ScanSessionRecoveryService>();
        services.AddSingleton<Infrastructure.ThemeService>();
        services.AddSingleton<IDatabaseMaintenance>(sp => sp.GetRequiredService<StorageDbContext>());

        services.AddSingleton<FileScanner>(sp => new FileScanner(
            sp.GetRequiredService<IScanRepository>(),
            sp.GetRequiredService<ILogger<FileScanner>>(),
            sp.GetRequiredService<IFileIdentityProvider>(),
            sp.GetRequiredService<IScanErrorRepository>(),
            sp.GetRequiredService<IDirectoryFileIdentityProvider>()));
        services.AddSingleton<TurboFileScanner>(sp => new TurboFileScanner(
            sp.GetRequiredService<IScanRepository>(),
            sp.GetRequiredService<ILogger<TurboFileScanner>>(),
            sp.GetRequiredService<FileScanner>(),
            sp.GetRequiredService<IScanErrorRepository>()));
        services.AddSingleton<IFileScanner>(sp => sp.GetRequiredService<FileScanner>());

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
        services.AddSingleton<ICleanupEngine, CleanupEngine>();
        services.AddSingleton<IScanResultDeletionService, ScanResultDeletionService>();
        services.AddSingleton<IFileContentHasher, FileContentHasher>();
        services.AddSingleton<IFileSnapshotProvider, FileSnapshotProvider>();
        services.AddSingleton<INoFollowFileEnumerator, NoFollowFileEnumerator>();
        services.AddSingleton<ISmartCleanerService, SmartCleanerService>();

        services.AddSingleton<IDuplicateDetectionStrategy>(sp =>
            new ExactSha256Strategy(
                sp.GetRequiredService<IFileContentHasher>(),
                sp.GetRequiredService<IFileSnapshotProvider>()));
        services.AddSingleton<IDuplicateDetectionStrategy>(sp =>
            new NormalizedTextStrategy(
                sp.GetRequiredService<IFileSnapshotProvider>()));
        services.AddSingleton<IDuplicateDetectionStrategy>(sp =>
            new ImagePHashStrategy(
                sp.GetRequiredService<ISettingsSnapshotProvider>(),
                sp.GetRequiredService<IFileSnapshotProvider>()));
        services.AddSingleton<IDuplicateDetectionStrategy>(sp =>
            new VideoPHashStrategy(
                sp.GetRequiredService<ISettingsRepository>(),
                sp.GetRequiredService<ISettingsSnapshotProvider>(),
                sp.GetRequiredService<IFileSnapshotProvider>(),
                AppContext.BaseDirectory));

        services.AddSingleton<IDuplicateKeeperPolicy, DuplicateKeeperPolicy>();
        services.AddSingleton<IDuplicateFinderService>(sp =>
            new DuplicateFinderService(
                sp.GetRequiredService<IDuplicateRepository>(),
                sp.GetRequiredService<IDuplicateCandidateProvider>(),
                sp.GetRequiredService<IFileContentHasher>(),
                sp.GetServices<IDuplicateDetectionStrategy>(),
                sp.GetRequiredService<IDuplicateKeeperPolicy>(),
                sp.GetRequiredService<ILogger<DuplicateFinderService>>(),
                sp.GetRequiredService<IFileSnapshotProvider>()));
        services.AddSingleton<IDuplicateDeletionService, DuplicateDeletionService>();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ILocalDiagnosticsService, LocalDiagnosticsService>();
        services.AddSingleton<StartupRegistrationService>();
        services.AddSingleton<DesktopNotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<DesktopNotificationService>());
        services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
        services.AddSingleton<IDuplicatePreviewService, DuplicatePreviewService>();
        services.AddSingleton<ICommandRunner, CommandRunner>();

        services.AddTransient<DashboardViewModel>();
        services.AddTransient<ScanWorkspaceViewModel>();
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
        services.AddTransient<SpaceMapViewModel>();
        services.AddTransient<DriveHealthViewModel>();

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    private static string GetCurrentVersionText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
    }
}
