using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Targets the Windows Update download cache — the files in
/// %WINDIR%\SoftwareDistribution\Download that Windows keeps after applying
/// updates. These can safely be deleted at any time; Windows will re-download
/// whatever it needs for future updates.
/// </summary>
public sealed class WindowsUpdateCacheRule : ICleanupRule
{
    public string RuleId => "core.windows-update-cache";
    public string DisplayName => Loc.Get("Rule_WindowsUpdateCache_Name");
    public CleanupCategory Category => CleanupCategory.WindowsUpdateCache;

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "SoftwareDistribution", "Download");

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!Directory.Exists(CachePath)) yield break;

        long totalBytes = 0;
        int fileCount = 0;
        try
        {
            // DirectoryInfo carries the size from the enumeration; the string overload
            // would force a second metadata call per file, and this cache routinely
            // holds tens of thousands of them.
            foreach (var file in new DirectoryInfo(CachePath).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    totalBytes += file.Length;
                    fileCount++;
                }
                catch { /* best-effort */ }
            }
        }
        catch (UnauthorizedAccessException) { /* needs admin — report what we found */ }

        if (fileCount == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = RuleId,
            Title = Loc.Format("Rule_WindowsUpdateCache_Title", fileCount.ToString("N0")),
            Description = Loc.Format(
                "Rule_WindowsUpdateCache_Description",
                FormatBytes(totalBytes)),
            Category = Category,
            Risk = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths = [CachePath],
            IsSystemPath = true,
        };
    }

    private static string FormatBytes(long b) => ByteFormat.Format(b);
}
