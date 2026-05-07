using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Finds installer files (.msi, .exe, .msix, etc.) inside the Downloads folder.
///
/// When <see cref="AppSettings.ClearEntireDownloads"/> is enabled it also
/// surfaces a second, distinct suggestion to delete ALL content from Downloads
/// (not just installer files). That suggestion is marked Medium risk so the user
/// always reviews it explicitly before acting.
/// </summary>
public sealed class DownloadedInstallersRule : ICleanupRule
{
    private readonly IScanRepository _repo;
    private readonly Func<string> _getDownloadsPath;

    public string RuleId => "core.downloaded-installers";
    public string DisplayName => "Downloaded Installers";
    public CleanupCategory Category => CleanupCategory.DownloadedInstallers;

    private static readonly HashSet<string> InstallerExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".msp", ".msix", ".appx", ".appxbundle",
            ".pkg", ".dmg", ".iso", ".img",
        };

    public DownloadedInstallersRule(IScanRepository repo, Func<string> getDownloadsPath)
    {
        _repo = repo;
        _getDownloadsPath = getDownloadsPath;
    }

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Normalize: strip trailing separators then add exactly one so that a
        // downloads path of "C:\Users\foo\Downloads" cannot match a sibling
        // folder called "C:\Users\foo\Downloads Backup".
        var downloadsRoot = _getDownloadsPath()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var files = await _repo.GetLargestFilesAsync(sessionId, topN: 50_000, cancellationToken);

        // ── Suggestion 1: installer files only ──────────────────────────────
        var installers = files
            .Where(f =>
                InstallerExtensions.Contains(f.Extension) &&
                f.FullPath.StartsWith(downloadsRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (installers.Count > 0)
        {
            long totalBytes = installers.Sum(f => f.SizeBytes);
            yield return new CleanupSuggestion
            {
                Id = Guid.NewGuid(),
                RuleId = RuleId,
                Title = $"Downloaded installers ({installers.Count:N0} files)",
                Description = $"Installer files (.exe, .msi, .iso, …) in your Downloads folder. " +
                                 $"Estimated savings: {FormatBytes(totalBytes)}. " +
                                 "Review before deleting — some may be needed for reinstallation.",
                Category = Category,
                Risk = CleanupRisk.Low,
                EstimatedBytes = totalBytes,
                TargetPaths = installers.Select(f => f.FullPath).ToList(),
                IsSystemPath = false,
            };
        }

        // ── Suggestion 2 (optional): clear the entire Downloads folder ───────
        if (settings.ClearEntireDownloads && Directory.Exists(downloadsRoot))
        {
            await Task.Yield();

            // Collect individual file paths so FileDeleter can report
            // per-file success/failure rather than treating the folder as an
            // atomic unit (which would silently skip locked files).
            var allDownloads = files
                .Where(f => f.FullPath.StartsWith(downloadsRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();

            long totalDownloadBytes = allDownloads.Sum(f => f.SizeBytes);
            if (totalDownloadBytes > 0)
            {
                // Strip the trailing separator back off for display only.
                var displayPath = downloadsRoot.TrimEnd(Path.DirectorySeparatorChar);
                yield return new CleanupSuggestion
                {
                    Id = Guid.NewGuid(),
                    RuleId = "core.clear-downloads-folder",
                    Title = $"Clear entire Downloads folder ({allDownloads.Count:N0} files)",
                    Description = $"Removes ALL content from {displayPath}. This includes documents, " +
                                     $"archives, media, and any other files you may have downloaded. " +
                                     $"Estimated savings: {FormatBytes(totalDownloadBytes)}. " +
                                     "This action cannot be easily undone — use Recycle Bin mode.",
                    Category = Category,
                    Risk = CleanupRisk.Medium,
                    EstimatedBytes = totalDownloadBytes,
                    // Individual file paths — not the folder itself — so the
                    // Downloads directory is preserved and partial failures surface.
                    TargetPaths = allDownloads.Select(f => f.FullPath).ToList(),
                    IsSystemPath = false,
                };
            }
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1L << 30 => $"{bytes / (1L << 30):F1} GB",
            >= 1L << 20 => $"{bytes / (1L << 20):F1} MB",
            >= 1L << 10 => $"{bytes / (1L << 10):F1} KB",
            _ => $"{bytes} B",
        };
}
