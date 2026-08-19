using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Suggests deleting Explorer icon cache database files.
/// These are the iconcache_*.db files stored in the Explorer directory.
/// Windows rebuilds the icon cache automatically after the next restart or
/// when Explorer refreshes.
/// </summary>
public sealed class IconCacheRule : ICleanupRule
{
    public string RuleId => "core.icon-cache";
    public string DisplayName => Loc.Get("Rule_IconCache_Name");
    public CleanupCategory Category => CleanupCategory.IconCache;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        var explorerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Explorer");

        if (!Directory.Exists(explorerDir)) yield break;

        var paths = new List<string>();
        long totalBytes = 0;

        foreach (var file in Directory.EnumerateFiles(explorerDir, "iconcache_*.db"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length == 0) continue;
                totalBytes += info.Length;
                paths.Add(file);
            }
            catch { /* file locked — skip */ }
        }

        if (paths.Count == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = RuleId,
            Title = Loc.Format("Rule_IconCache_Title", paths.Count),
            Description = Loc.Format("Rule_IconCache_Description", FormatBytes(totalBytes)),
            Category = Category,
            Risk = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths = paths,
            IsSystemPath = false,
        };
    }

    private static string FormatBytes(long b) => ByteFormat.Format(b);
}
