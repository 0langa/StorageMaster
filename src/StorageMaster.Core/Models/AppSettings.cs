namespace StorageMaster.Core.Models;

/// <summary>Persisted application preferences.</summary>
public sealed class AppSettings
{
    public bool   PreferRecycleBin        { get; set; } = true;
    public bool   DryRunByDefault         { get; set; } = false;
    public int    LargeFileSizeMb         { get; set; } = 500;
    public int    OldFileAgeDays          { get; set; } = 365;
    public string DefaultScanPath         { get; set; } = @"C:\";
    public int    ScanParallelism         { get; set; } = 4;
    public bool   ShowHiddenFiles         { get; set; } = false;
    public bool   SkipSystemFolders       { get; set; } = true;
    public IList<string> ExcludedPaths    { get; set; } = [];
}
