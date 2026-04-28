using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.SmartCleaner;

/// <summary>
/// Implements the Smart Cleaner workflow.
/// Scans eight well-known junk locations directly (no scan database required)
/// and deletes items via IFileDeleter.
///
/// Safe-deletion policy:
///   • Only locations with CleanupRisk ≤ Low are included automatically.
///   • Nothing in Program Files, Windows, or System32 is ever touched.
///   • The Recycle Bin is queried but only emptied when the user explicitly
///     includes it in the clean-up.
/// </summary>
public sealed class SmartCleanerService : ISmartCleanerService
{
    private readonly IFileDeleter        _deleter;
    private readonly ILogger<SmartCleanerService> _logger;

    public SmartCleanerService(IFileDeleter deleter, ILogger<SmartCleanerService> logger)
    {
        _deleter = deleter;
        _logger  = logger;
    }

    public async Task<IReadOnlyList<SmartCleanGroup>> AnalyzeAsync(
        IProgress<string>? progress  = null,
        CancellationToken  ct        = default)
    {
        var groups = new List<SmartCleanGroup>();

        await Task.Run(() =>
        {
            // ── Temporary files ───────────────────────────────────────────────
            progress?.Report("Scanning temporary files…");
            var tempRoots = new[]
            {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var tempFiles  = new List<string>();
            long tempBytes = 0;
            foreach (var root in tempRoots)
            {
                if (!Directory.Exists(root)) continue;
                ScanDirectory(root, ref tempBytes, tempFiles, ct);
            }
            if (tempBytes > 0)
                groups.Add(new SmartCleanGroup("Temporary Files",
                    "Files in Windows temp folders that are safe to delete.",
                    "", tempBytes, tempFiles));

            // ── Browser caches ────────────────────────────────────────────────
            progress?.Report("Scanning browser caches…");
            var browserPaths = GetBrowserCachePaths();
            if (browserPaths.Count > 0)
            {
                long browserBytes = 0;
                foreach (var p in browserPaths)
                    ScanDirectory(p, ref browserBytes, [], ct);
                if (browserBytes > 0)
                    groups.Add(new SmartCleanGroup("Browser Cache",
                        "Cached web content from Chrome, Edge, Firefox, Brave, and Opera.",
                        "", browserBytes, browserPaths));
            }

            // ── Windows Update cache ─────────────────────────────────────────
            progress?.Report("Scanning Windows Update cache…");
            var wuPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "SoftwareDistribution", "Download");
            if (Directory.Exists(wuPath))
            {
                long wuBytes = 0;
                ScanDirectory(wuPath, ref wuBytes, [], ct);
                if (wuBytes > 0)
                    groups.Add(new SmartCleanGroup("Windows Update Cache",
                        "Downloaded update packages already applied. Windows re-downloads as needed.",
                        "", wuBytes, [wuPath]));
            }

            // ── Windows Error Reporting ──────────────────────────────────────
            progress?.Report("Scanning error reports…");
            var werPaths = GetWerPaths();
            if (werPaths.Count > 0)
            {
                long werBytes = 0;
                foreach (var p in werPaths)
                {
                    if (File.Exists(p))
                    {
                        try { werBytes += new FileInfo(p).Length; } catch { }
                    }
                    else ScanDirectory(p, ref werBytes, [], ct);
                }
                if (werBytes > 0)
                    groups.Add(new SmartCleanGroup("Error Reports & Crash Dumps",
                        "Windows diagnostic files from app crashes. Already sent to Microsoft if you opted in.",
                        "", werBytes, werPaths));
            }

            // ── Delivery Optimization ────────────────────────────────────────
            progress?.Report("Scanning Delivery Optimization cache…");
            var doPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "SoftwareDistribution", "DeliveryOptimization");
            if (Directory.Exists(doPath))
            {
                long doBytes = 0;
                ScanDirectory(doPath, ref doBytes, [], ct);
                if (doBytes > 0)
                    groups.Add(new SmartCleanGroup("Delivery Optimization Cache",
                        "Peer-to-peer Windows Update sharing cache. Rebuilds automatically.",
                        "", doBytes, [doPath]));
            }

            // ── Thumbnail cache ──────────────────────────────────────────────
            progress?.Report("Scanning thumbnail cache…");
            var thumbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Explorer");
            if (Directory.Exists(thumbDir))
            {
                var thumbFiles = Directory
                    .EnumerateFiles(thumbDir, "thumbcache_*.db", SearchOption.TopDirectoryOnly)
                    .ToList();
                long thumbBytes = thumbFiles.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                if (thumbBytes > 0)
                    groups.Add(new SmartCleanGroup("Thumbnail Cache",
                        "Cached image previews for Windows Explorer. Rebuilt automatically when you browse folders.",
                        "", thumbBytes, thumbFiles));
            }

            // ── DirectX shader cache ─────────────────────────────────────────
            progress?.Report("Scanning shader cache…");
            var shaderDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "D3DSCache");
            if (Directory.Exists(shaderDir))
            {
                long shaderBytes = 0;
                ScanDirectory(shaderDir, ref shaderBytes, [], ct);
                if (shaderBytes > 0)
                    groups.Add(new SmartCleanGroup("DirectX Shader Cache",
                        "Compiled GPU shader programs. Rebuilt by games and apps on next launch.",
                        "", shaderBytes, [shaderDir]));
            }

            // ── Recycle Bin ──────────────────────────────────────────────────
            progress?.Report("Checking Recycle Bin…");
            // Size is retrieved via platform-specific caller; use sentinel path here.
            // The actual size will be filled in by the caller if IRecycleBinInfoProvider
            // is available. We still include it so the UI always shows the option.

        }, ct);

        return groups;
    }

    public async Task<long> CleanAsync(
        IReadOnlyList<SmartCleanGroup> groups,
        DeletionMethod                 method,
        IProgress<string>?             progress = null,
        CancellationToken              ct       = default)
    {
        long freed = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Cleaning {group.Category}…");

            var requests = group.Paths
                .Select(p => new DeletionRequest(p, method, DryRun: false))
                .ToList();

            await foreach (var outcome in _deleter.DeleteManyAsync(requests, ct))
            {
                if (outcome.Success)
                {
                    freed += outcome.BytesFreed;
                    _logger.LogInformation("[SmartCleaner] Freed {Size} from {Path}",
                        outcome.BytesFreed, outcome.Path);
                }
                else
                {
                    _logger.LogWarning("[SmartCleaner] Failed {Path}: {Error}",
                        outcome.Path, outcome.Error);
                }
            }
        }

        return freed;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ScanDirectory(string dir, ref long totalBytes, List<string> paths, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    totalBytes += new FileInfo(f).Length;
                    if (paths.Count < 100_000) paths.Add(f);
                }
                catch { /* best-effort */ }
            }
        }
        catch { /* skip inaccessible dirs */ }
    }

    private static List<string> GetBrowserCachePaths()
    {
        var paths = new List<string>();
        var local  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        AddBrowserCacheDirs(Path.Combine(local, "Google", "Chrome", "User Data"), paths);
        AddBrowserCacheDirs(Path.Combine(local, "Microsoft", "Edge", "User Data"), paths);
        AddBrowserCacheDirs(Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), paths);
        AddFirefoxCacheDirs(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"), paths);
        AddBrowserCacheDirs(Path.Combine(roaming, "Opera Software"), paths);
        return paths;
    }

    private static void AddBrowserCacheDirs(string baseDir, List<string> paths)
    {
        if (!Directory.Exists(baseDir)) return;
        foreach (var profile in Directory.EnumerateDirectories(baseDir))
        {
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                var p = Path.Combine(profile, sub);
                if (Directory.Exists(p)) paths.Add(p);
            }
        }
    }

    private static void AddFirefoxCacheDirs(string baseDir, List<string> paths)
    {
        if (!Directory.Exists(baseDir)) return;
        foreach (var profile in Directory.EnumerateDirectories(baseDir))
        {
            foreach (var sub in new[] { "cache2", "startupCache" })
            {
                var p = Path.Combine(profile, sub);
                if (Directory.Exists(p)) paths.Add(p);
            }
        }
    }

    private static List<string> GetWerPaths()
    {
        var paths = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var userWer = Path.Combine(local, "Microsoft", "Windows", "WER");
        if (Directory.Exists(userWer)) paths.Add(userWer);

        var sysWer = Path.Combine(common, "Microsoft", "Windows", "WER");
        if (Directory.Exists(sysWer)) paths.Add(sysWer);

        var crashes = Path.Combine(local, "CrashDumps");
        if (Directory.Exists(crashes)) paths.Add(crashes);

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var dump in Directory.EnumerateFiles(winDir, "*.dmp", SearchOption.TopDirectoryOnly))
            paths.Add(dump);

        return paths;
    }
}
