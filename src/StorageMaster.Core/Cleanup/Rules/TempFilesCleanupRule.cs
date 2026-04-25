using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Suggests deleting files in Windows temp folders and files with .tmp/.temp extensions.
/// Risk is Low — temp files should not be referenced by running processes,
/// but we cannot guarantee this without kernel-level handle checks (v2 concern).
/// </summary>
public sealed class TempFilesCleanupRule : ICleanupRule
{
    private readonly IScanRepository _repo;

    public string RuleId      => "core.temp-files";
    public string DisplayName => "Temporary Files";
    public CleanupCategory Category => CleanupCategory.TempFiles;

    // Known safe temp locations.
    private static readonly string[] TempRoots =
    [
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
    ];

    private static readonly HashSet<string> TempExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tmp", ".temp", ".chk", ".$$$", ".gid" };

    public TempFilesCleanupRule(IScanRepository repo) => _repo = repo;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var files = await _repo.GetLargestFilesAsync(sessionId, topN: 50_000, cancellationToken);

        var tempRootsNorm = TempRoots
            .Select(r => r.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var targets = files
            .Where(f => IsTemp(f, tempRootsNorm))
            .ToList();

        if (targets.Count == 0) yield break;

        long totalBytes = targets.Sum(f => f.SizeBytes);
        var  paths      = targets.Select(f => f.FullPath).ToList();

        yield return new CleanupSuggestion
        {
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Temporary files ({targets.Count:N0} files)",
            Description    = $"Files in Windows temp folders and files with temporary extensions. " +
                             $"Estimated savings: {FormatBytes(totalBytes)}.",
            Category       = Category,
            Risk           = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths    = paths,
            IsSystemPath   = false,
        };
    }

    private static bool IsTemp(FileEntry f, string[] tempRoots)
    {
        if (TempExtensions.Contains(f.Extension))
            return true;

        foreach (var root in tempRoots)
            if (f.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1L << 30 => $"{bytes / (1L << 30):F1} GB",
            >= 1L << 20 => $"{bytes / (1L << 20):F1} MB",
            >= 1L << 10 => $"{bytes / (1L << 10):F1} KB",
            _           => $"{bytes} B",
        };
}
