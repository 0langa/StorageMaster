namespace StorageMaster.Core.Models;

/// <summary>Persisted application preferences.</summary>
public sealed class AppSettings
{
    // ── Deletion ────────────────────────────────────────────────────────────
    public bool   PreferRecycleBin        { get; set; } = true;
    public bool   DryRunByDefault         { get; set; } = false;

    // ── Thresholds ──────────────────────────────────────────────────────────
    public int    LargeFileSizeMb         { get; set; } = 500;
    public int    OldFileAgeDays          { get; set; } = 365;

    // ── Scan ────────────────────────────────────────────────────────────────
    public string DefaultScanPath         { get; set; } = @"C:\";
    public int    ScanParallelism         { get; set; } = 4;
    public bool   ShowHiddenFiles         { get; set; } = false;
    public bool   SkipSystemFolders       { get; set; } = true;
    public bool   UseTurboScanner         { get; set; } = false;
    public IList<string> ExcludedPaths    { get; set; } = [];

    // ── Cleanup rule toggles (persisted, can be overridden per-session) ─────
    public bool   CleanRecycleBin           { get; set; } = true;
    public bool   CleanTempFiles            { get; set; } = true;
    public bool   CleanDownloadedInstallers { get; set; } = true;
    public bool   ClearEntireDownloads      { get; set; } = false;   // sub-option
    public bool   CleanCacheFolders         { get; set; } = true;
    public bool   CleanBrowserCache         { get; set; } = true;
    public bool   CleanWindowsUpdateCache   { get; set; } = true;
    public bool   CleanDeliveryOptimization { get; set; } = true;
    public bool   CleanWindowsErrorReports  { get; set; } = true;
    public bool   CleanProgramLeftovers     { get; set; } = true;
    public bool   CleanLargeOldFiles        { get; set; } = false;   // off by default — higher risk
}
