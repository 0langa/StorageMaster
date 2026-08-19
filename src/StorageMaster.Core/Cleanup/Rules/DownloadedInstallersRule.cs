using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

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
    public string DisplayName => Loc.Get("Rule_DownloadedInstallers_Name");
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

        // Both suggestions below are scoped to Downloads, so narrow inside the pager
        // rather than materializing every file of the scan session first. The
        // containment check is repeated per suggestion: it is what keeps a target
        // path inside Downloads, and it stays visible at the point of use.
        var files = await ScanFilePager.LoadAllAsync(
            _repo,
            sessionId,
            file => IsInDownloads(file.FullPath, downloadsRoot),
            cancellationToken);

        // ── Suggestion 1: installer files only ──────────────────────────────
        var installers = files
            .Where(f =>
                InstallerExtensions.Contains(f.Extension) &&
                IsInDownloads(f.FullPath, downloadsRoot) &&
                f.Identity is not null)
            .ToList();

        if (installers.Count > 0)
        {
            long totalBytes = installers.Sum(f => f.SizeBytes);
            yield return new CleanupSuggestion
            {
                Id = Guid.NewGuid(),
                RuleId = RuleId,
                Title = Loc.Format(
                    "Rule_DownloadedInstallers_Title",
                    installers.Count.ToString("N0")),
                Description = Loc.Format(
                    "Rule_DownloadedInstallers_Description",
                    FormatBytes(totalBytes)),
                Category = Category,
                Risk = CleanupRisk.Low,
                EstimatedBytes = totalBytes,
                TargetPaths = installers.Select(f => f.FullPath).ToList(),
                ExpectedFileSnapshots = CreateSnapshots(installers),
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
                .Where(f => IsInDownloads(f.FullPath, downloadsRoot))
                .ToList();

            long totalDownloadBytes = allDownloads.Sum(f => f.SizeBytes);
            if (totalDownloadBytes > 0 && allDownloads.All(static file => file.Identity is not null))
            {
                // Strip the trailing separator back off for display only.
                var displayPath = downloadsRoot.TrimEnd(Path.DirectorySeparatorChar);
                yield return new CleanupSuggestion
                {
                    Id = Guid.NewGuid(),
                    RuleId = "core.clear-downloads-folder",
                    Title = Loc.Format(
                        "Safety_ClearDownloads_Title",
                        allDownloads.Count.ToString("N0")),
                    Description = Loc.Format(
                        "Safety_ClearDownloads_Description",
                        displayPath,
                        FormatBytes(totalDownloadBytes)),
                    Category = Category,
                    Risk = CleanupRisk.Medium,
                    EstimatedBytes = totalDownloadBytes,
                    SupportsPermanentDelete = false,
                    // Individual file paths — not the folder itself — so the
                    // Downloads directory is preserved and partial failures surface.
                    TargetPaths = allDownloads.Select(f => f.FullPath).ToList(),
                    ExpectedFileSnapshots = CreateSnapshots(allDownloads),
                    IsSystemPath = false,
                };
            }
        }
    }

    private static bool IsInDownloads(string path, string downloadsRoot)
    {
        try
        {
            return ScanOptionValidator.IsPathEqualOrUnder(path, downloadsRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, FileSnapshot> CreateSnapshots(
        IEnumerable<FileEntry> files) => files.ToDictionary(
        static file => file.FullPath,
        static file => new FileSnapshot(
            file.FullPath,
            file.Identity!,
            file.SizeBytes,
            file.ModifiedUtc,
            file.Attributes),
        StringComparer.OrdinalIgnoreCase);

    private static string FormatBytes(long bytes) => ByteFormat.Format(bytes);
}
