using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Identifies files that are both large (> LargeFileSizeMb) and old (> OldFileAgeDays).
/// Risk is Medium — these are user files that may be intentionally kept.
/// We yield one suggestion per file so the user can pick-and-choose in the UI.
/// </summary>
public sealed class LargeOldFilesCleanupRule : ICleanupRule
{
    private readonly IScanRepository _repo;

    public string RuleId      => "core.large-old-files";
    public string DisplayName => "Large Old Files";
    public CleanupCategory Category => CleanupCategory.LargeOldFiles;

    // Paths we refuse to suggest regardless of size/age — protect the user from accidents.
    private static readonly string[] ProtectedPrefixes =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
    ];

    public LargeOldFilesCleanupRule(IScanRepository repo) => _repo = repo;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long thresholdBytes = (long)settings.LargeFileSizeMb * 1024 * 1024;
        var  cutoff         = DateTime.UtcNow.AddDays(-settings.OldFileAgeDays);

        // Fetch a generous top-N; we filter by age below.
        var candidates = await _repo.GetLargestFilesAsync(sessionId, topN: 1000, cancellationToken);

        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.SizeBytes < thresholdBytes)
                break; // results are sorted descending by size

            if (file.ModifiedUtc > cutoff)
                continue;

            if (IsProtected(file.FullPath))
                continue;

            yield return new CleanupSuggestion
            {
                Id             = Guid.NewGuid(),
                RuleId         = RuleId,
                Title          = $"Large old file: {file.FileName}",
                Description    = $"{FormatBytes(file.SizeBytes)}, last modified {file.ModifiedUtc:yyyy-MM-dd}. " +
                                 $"Path: {file.FullPath}",
                Category       = Category,
                Risk           = CleanupRisk.Medium,
                EstimatedBytes = file.SizeBytes,
                TargetPaths    = [file.FullPath],
                IsSystemPath   = false,
            };
        }
    }

    private static bool IsProtected(string path) =>
        ProtectedPrefixes.Any(p =>
            !string.IsNullOrEmpty(p) &&
            path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1L << 30 => $"{bytes / (1L << 30):F1} GB",
            >= 1L << 20 => $"{bytes / (1L << 20):F1} MB",
            >= 1L << 10 => $"{bytes / (1L << 10):F1} KB",
            _           => $"{bytes} B",
        };
}
