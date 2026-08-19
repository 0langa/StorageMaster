using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageMaster.Core.Models;

public enum ThemePreference
{
    Default,
    Light,
    Dark,
}

/// <summary>
/// Which language the interface uses.
/// <para>
/// This is not cosmetic. WinUI supplies its own text for built-in controls — a
/// ToggleSwitch renders "On"/"Off" or "Ein"/"Aus" — and it follows the Windows
/// language, not the app's strings. On a German Windows install that produced an
/// English app with German switches. Pinning the language makes both halves agree.
/// </para>
/// </summary>
public enum UiLanguage
{
    /// <summary>Follow Windows.</summary>
    System,
    English,
    German,
}

public enum UiDensity
{
    Compact,
    Default,
    Comfortable,
}

/// <summary>Persisted application preferences.</summary>
public sealed class AppSettings
{
    // ── Deletion ────────────────────────────────────────────────────────────
    public bool PreferRecycleBin { get; set; } = true;
    public bool DryRunByDefault { get; set; } = false;

    // ── Thresholds ──────────────────────────────────────────────────────────
    public int LargeFileSizeMb { get; set; } = 500;
    public int OldFileAgeDays { get; set; } = 365;

    // ── Scan ────────────────────────────────────────────────────────────────
    public string DefaultScanPath { get; set; } = @"C:\";
    public int ScanParallelism { get; set; } = 4;
    public bool ShowHiddenFiles { get; set; } = false;
    public bool SkipSystemFolders { get; set; } = true;
    public bool UseTurboScanner { get; set; } = false;
    public IList<string> ExcludedPaths { get; set; } = [];
    public ThemePreference Theme { get; set; } = ThemePreference.Default;

    /// <summary>
    /// Identifier of the selected accent, resolved through
    /// <see cref="Theming.ThemeCatalog.ResolveAccent"/>. Stored as a string rather
    /// than an enum so an accent retired in a future version degrades to the
    /// default instead of failing to deserialise.
    /// </summary>
    public string AccentId { get; set; } = Theming.ThemeCatalog.DefaultAccentId;

    /// <summary>Interface language. See <see cref="UiLanguage"/> for why this exists.</summary>
    public UiLanguage Language { get; set; } = UiLanguage.System;
    public int ScanHistoryRetentionDays { get; set; } = 365;

    // ── Cleanup rule toggles (persisted, can be overridden per-session) ─────
    public bool CleanRecycleBin { get; set; } = true;
    public bool CleanTempFiles { get; set; } = true;
    public bool CleanDownloadedInstallers { get; set; } = true;
    public bool ClearEntireDownloads { get; set; } = false;   // sub-option
    public bool CleanCacheFolders { get; set; } = true;
    public bool CleanBrowserCache { get; set; } = true;
    public bool CleanWindowsUpdateCache { get; set; } = true;
    public bool CleanDeliveryOptimization { get; set; } = true;
    public bool CleanWindowsErrorReports { get; set; } = true;
    public bool CleanProgramLeftovers { get; set; } = false;
    public bool CleanLargeOldFiles { get; set; } = false;   // off by default — higher risk

    // ── Update preferences ──────────────────────────────────────────────────
    public bool CheckOnStartup { get; set; } = true;
    public bool IncludePrerelease { get; set; } = false;
    public bool RequireSignedUpdates { get; set; } = false;

    // ── Background / tray / scheduler ──────────────────────────────────────
    public bool MinimizeToTray { get; set; } = false;
    public bool StartTrayOnLogin { get; set; } = false;
    public bool EnableLowDiskNotifications { get; set; } = true;
    public bool EnableDriveHealthNotifications { get; set; } = true;
    public int LowDiskWarningPercent { get; set; } = 15;
    public int LowDiskCriticalPercent { get; set; } = 5;
    public bool ScheduledTasksEnabled { get; set; } = false;
    public IList<ScheduledJobDefinition> ScheduledJobs { get; set; } = [];
    public IDictionary<string, string> LowDiskNotificationState { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IDictionary<string, string> DriveHealthNotificationState { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ── New cleanup rule toggles ─────────────────────────────────────────────
    public bool CleanThumbnailCache { get; set; } = true;
    public bool CleanIconCache { get; set; } = true;
    public bool CleanFontCache { get; set; } = false;  // rebuilds on boot, opt-in
    public bool CleanDnsCache { get; set; } = true;
    public bool CleanPrefetchFiles { get; set; } = false;  // medium risk, needs elevation, opt-in
    public bool CleanStoreLogs { get; set; } = true;

    // ── Deduplication defaults ──────────────────────────────────────────────
    public int DuplicateMinimumSizeMb { get; set; } = 1;
    public KeeperPolicy DuplicateKeeperPolicy { get; set; } = KeeperPolicy.Newest;
    public bool DuplicateUseNormalizedText { get; set; } = false;
    public bool DuplicateUseImagePHash { get; set; } = false;
    public bool DuplicateUseVideoPHash { get; set; } = false;
    public int DuplicateImagePHashThreshold { get; set; } = 6;
    public int DuplicateVideoFrameThreshold { get; set; } = 8;
    public int DuplicateMaxVideoDurationSeconds { get; set; } = 1800;
    /// <summary>
    /// Absolute path to ffmpeg.exe. Legacy directory values are normalized on load.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    // ── UI preferences ─────────────────────────────────────────────────────
    public UiDensity UiDensity { get; set; } = UiDensity.Default;
    public bool ReduceAnimations { get; set; } = false;
    public string DefaultLandingPage { get; set; } = "Dashboard";
    public int DefaultResultsPageSize { get; set; } = 100;
    public string DefaultDuplicatesReviewMode { get; set; } = "Exact";
    public bool ExpandAdvancedOptionsByDefault { get; set; } = false;

    // Preserve forwards-compatible JSON fields on load-modify-save cycles.
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}
