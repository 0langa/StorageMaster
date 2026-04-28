using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
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
    public string RuleId      => "core.windows-update-cache";
    public string DisplayName => "Windows Update Cache";
    public CleanupCategory Category => CleanupCategory.WindowsUpdateCache;

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "SoftwareDistribution", "Download");

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!Directory.Exists(CachePath)) yield break;

        long totalBytes = 0;
        int  fileCount  = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(CachePath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    totalBytes += new FileInfo(f).Length;
                    fileCount++;
                }
                catch { /* best-effort */ }
            }
        }
        catch (UnauthorizedAccessException) { /* needs admin — report what we found */ }

        if (fileCount == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Windows Update cache ({fileCount:N0} files)",
            Description    = "Downloaded update packages that have already been applied. " +
                             "Windows will re-download them only if the same update needs " +
                             $"to be re-applied. Estimated savings: {FormatBytes(totalBytes)}.",
            Category       = Category,
            Risk           = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths    = [CachePath],
            IsSystemPath   = true,
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
