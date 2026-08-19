using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Suggests deleting the Windows font rendering cache.
/// The cache is stored in two locations: one for the LocalService account
/// (system-wide font cache service) and one per-user.
/// Windows Font Cache Service rebuilds it automatically on the next boot.
/// </summary>
public sealed class FontCacheRule : ICleanupRule
{
    public string RuleId => "core.font-cache";
    public string DisplayName => Loc.Get("Rule_FontCache_Name");
    public CleanupCategory Category => CleanupCategory.FontCache;

    private static IEnumerable<string> GetCandidatePaths()
    {
        // System-wide font cache (Font Cache Service)
        var systemFontCache = Path.Combine(
            @"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache");
        if (Directory.Exists(systemFontCache)) yield return systemFontCache;

        // Per-user font cache
        var userFontCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "FontCache");
        if (Directory.Exists(userFontCache)) yield return userFontCache;
    }

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        long totalBytes = 0;
        var paths = new List<string>();

        foreach (var dir in GetCandidatePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Sizes come from the enumeration itself — the string overload would
                // discard them and re-stat every file.
                long dirSize = new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(file => { try { return file.Length; } catch { return 0L; } });
                if (dirSize == 0) continue;
                totalBytes += dirSize;
                paths.Add(dir);
            }
            catch { /* may require elevation — skip inaccessible dirs */ }
        }

        if (paths.Count == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = RuleId,
            Title = Loc.Format("Rule_FontCache_Title", paths.Count),
            Description = Loc.Format("Rule_FontCache_Description", FormatBytes(totalBytes)),
            Category = Category,
            Risk = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths = paths,
            IsSystemPath = true,
        };
    }

    private static string FormatBytes(long b) => ByteFormat.Format(b);
}
