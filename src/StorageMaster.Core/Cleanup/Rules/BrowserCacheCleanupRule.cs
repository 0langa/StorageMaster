using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Identifies cache directories for the three most common Windows browsers:
/// Google Chrome, Microsoft Edge, and Mozilla Firefox.
///
/// Only cache directories are targeted — bookmarks, passwords, extensions,
/// and profile data are never included.
/// </summary>
public sealed class BrowserCacheCleanupRule : ICleanupRule
{
    public string RuleId      => "core.browser-cache";
    public string DisplayName => "Browser Cache";
    public CleanupCategory Category => CleanupCategory.BrowserCache;

    private static IEnumerable<string> GetCandidatePaths()
    {
        var local  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Chrome — one cache dir per profile
        var chromeBase = Path.Combine(local, "Google", "Chrome", "User Data");
        if (Directory.Exists(chromeBase))
        {
            // Default profile + named profiles (Profile 1, Profile 2, …)
            foreach (var profile in Directory.EnumerateDirectories(chromeBase))
            {
                var cache = Path.Combine(profile, "Cache");
                if (Directory.Exists(cache))     yield return cache;
                var codeCache = Path.Combine(profile, "Code Cache");
                if (Directory.Exists(codeCache)) yield return codeCache;
                var gpuCache = Path.Combine(profile, "GPUCache");
                if (Directory.Exists(gpuCache))  yield return gpuCache;
            }
        }

        // Microsoft Edge (Chromium-based) — same structure as Chrome
        var edgeBase = Path.Combine(local, "Microsoft", "Edge", "User Data");
        if (Directory.Exists(edgeBase))
        {
            foreach (var profile in Directory.EnumerateDirectories(edgeBase))
            {
                var cache = Path.Combine(profile, "Cache");
                if (Directory.Exists(cache))     yield return cache;
                var codeCache = Path.Combine(profile, "Code Cache");
                if (Directory.Exists(codeCache)) yield return codeCache;
                var gpuCache = Path.Combine(profile, "GPUCache");
                if (Directory.Exists(gpuCache))  yield return gpuCache;
            }
        }

        // Firefox — cache2 inside each profile
        var firefoxBase = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxBase))
        {
            foreach (var profile in Directory.EnumerateDirectories(firefoxBase))
            {
                var cache = Path.Combine(profile, "cache2");
                if (Directory.Exists(cache)) yield return cache;
                var startupCache = Path.Combine(profile, "startupCache");
                if (Directory.Exists(startupCache)) yield return startupCache;
            }
        }

        // Opera GX
        var operaBase = Path.Combine(roaming, "Opera Software");
        if (Directory.Exists(operaBase))
        {
            foreach (var profile in Directory.EnumerateDirectories(operaBase))
            {
                var cache = Path.Combine(profile, "Cache");
                if (Directory.Exists(cache)) yield return cache;
            }
        }

        // Brave
        var braveBase = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data");
        if (Directory.Exists(braveBase))
        {
            foreach (var profile in Directory.EnumerateDirectories(braveBase))
            {
                var cache = Path.Combine(profile, "Cache");
                if (Directory.Exists(cache)) yield return cache;
                var gpuCache = Path.Combine(profile, "GPUCache");
                if (Directory.Exists(gpuCache)) yield return gpuCache;
            }
        }
    }

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        long totalBytes = 0;
        int  dirCount   = 0;
        var  paths      = new List<string>();

        foreach (var dir in GetCandidatePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long dirSize = Directory
                    .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                if (dirSize == 0) continue;
                totalBytes += dirSize;
                dirCount++;
                paths.Add(dir);
            }
            catch { /* browser may be running — skip inaccessible dirs */ }
        }

        if (paths.Count == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Browser cache ({dirCount} cache folder(s))",
            Description    = "Cached web content from Chrome, Edge, Firefox, Brave, and Opera. " +
                             "Pages will load slightly slower after first visit until the cache " +
                             $"rebuilds. Estimated savings: {FormatBytes(totalBytes)}.",
            Category       = Category,
            Risk           = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths    = paths,
            IsSystemPath   = false,
        };
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (1L << 20):F1} MB",
        >= 1L << 10 => $"{b / (1L << 10):F1} KB",
        _           => $"{b} B",
    };
}
