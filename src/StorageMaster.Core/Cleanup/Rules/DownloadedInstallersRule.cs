using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Finds installer files (.msi, .exe, .msix, etc.) inside the Downloads folder.
/// Risk is Low — these are typically safe to delete once software is installed.
/// </summary>
public sealed class DownloadedInstallersRule : ICleanupRule
{
    private readonly IScanRepository _repo;

    public string RuleId      => "core.downloaded-installers";
    public string DisplayName => "Downloaded Installers";
    public CleanupCategory Category => CleanupCategory.DownloadedInstallers;

    private static readonly HashSet<string> InstallerExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".msp", ".msix", ".appx", ".appxbundle",
            ".pkg", ".dmg", ".iso", ".img",
        };

    public DownloadedInstallersRule(IScanRepository repo) => _repo = repo;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var downloadsPath = GetDownloadsPath();
        var files = await _repo.GetLargestFilesAsync(sessionId, topN: 50_000, cancellationToken);

        var installers = files
            .Where(f =>
                InstallerExtensions.Contains(f.Extension) &&
                f.FullPath.StartsWith(downloadsPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (installers.Count == 0) yield break;

        long totalBytes = installers.Sum(f => f.SizeBytes);

        yield return new CleanupSuggestion
        {
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Downloaded installers ({installers.Count:N0} files)",
            Description    = $"Installer files in your Downloads folder. " +
                             $"Estimated savings: {FormatBytes(totalBytes)}. " +
                             "Review before deleting — some may be needed for reinstallation.",
            Category       = Category,
            Risk           = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths    = installers.Select(f => f.FullPath).ToList(),
            IsSystemPath   = false,
        };
    }

    private static string GetDownloadsPath()
    {
        // SHGetKnownFolderPath for FOLDERID_Downloads is the correct approach;
        // using the user profile fallback for now (works on all Windows versions).
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads");
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
