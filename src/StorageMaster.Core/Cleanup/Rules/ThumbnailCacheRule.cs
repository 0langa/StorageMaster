using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Suggests deleting Explorer thumbnail cache database files.
/// These are the thumbcache_*.db files stored in the Explorer directory.
/// Windows rebuilds them on demand whenever a folder containing images is opened.
/// </summary>
public sealed class ThumbnailCacheRule : ICleanupRule
{
    public string RuleId => "core.thumbnail-cache";
    public string DisplayName => "Thumbnail Cache";
    public CleanupCategory Category => CleanupCategory.ThumbnailCache;

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

        foreach (var file in Directory.EnumerateFiles(explorerDir, "thumbcache_*.db"))
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
            Title = $"Thumbnail cache ({paths.Count} file(s))",
            Description = $"Explorer thumbnail database files. Windows regenerates them automatically " +
                             $"when folders with images are opened. Estimated savings: {FormatBytes(totalBytes)}.",
            Category = Category,
            Risk = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths = paths,
            IsSystemPath = false,
        };
    }

    private static string FormatBytes(long b) => ByteFormat.Format(b);
}
