using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Suggests deleting Windows Prefetch files (.pf) from C:\Windows\Prefetch.
///
/// Prefetch files help Windows launch applications faster by pre-loading
/// data into memory. Deleting them causes a slight slowdown on the first
/// launch of each application after cleanup — Windows rebuilds them within
/// a few days of normal use.
///
/// Requires administrator elevation to access C:\Windows\Prefetch.
/// This rule yields no suggestions when the app is not running elevated.
/// </summary>
public sealed class PrefetchFilesRule : ICleanupRule
{
    private readonly IAdminService _adminService;

    public string RuleId => "core.prefetch-files";
    public string DisplayName => "Prefetch Files";
    public CleanupCategory Category => CleanupCategory.PrefetchFiles;

    public PrefetchFilesRule(IAdminService adminService)
        => _adminService = adminService;

    private static readonly string PrefetchDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        // Cannot read or delete prefetch files without elevation.
        if (!_adminService.IsRunningAsAdmin) yield break;
        if (!Directory.Exists(PrefetchDir)) yield break;

        var paths = new List<string>();
        long totalBytes = 0;

        foreach (var file in Directory.EnumerateFiles(PrefetchDir, "*.pf"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length == 0) continue;
                totalBytes += info.Length;
                paths.Add(file);
            }
            catch { /* skip locked/inaccessible files */ }
        }

        if (paths.Count == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = RuleId,
            Title = $"Prefetch files ({paths.Count:N0} files)",
            Description = $"App launch prefetch data in C:\\Windows\\Prefetch. Windows rebuilds these " +
                             $"within days of normal use. First launch of each app may be slightly slower. " +
                             $"Estimated savings: {FormatBytes(totalBytes)}.",
            Category = Category,
            Risk = CleanupRisk.Medium,
            EstimatedBytes = totalBytes,
            TargetPaths = paths,
            IsSystemPath = true,
        };
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (1L << 20):F1} MB",
        >= 1L << 10 => $"{b / (1L << 10):F1} KB",
        _ => $"{b} B",
    };
}
