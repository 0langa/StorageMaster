using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Finds known safe cache folders under AppData\Local and suggests clearing them.
/// Risk is Low for most entries; each folder gets its own suggestion so the user
/// can review individually.
/// </summary>
public sealed class CacheFolderCleanupRule : ICleanupRule
{
    private readonly IScanRepository _repo;

    public string RuleId      => "core.cache-folders";
    public string DisplayName => "Application Caches";
    public CleanupCategory Category => CleanupCategory.CacheFolders;

    // (subpath-under-LocalAppData, display-name, risk)
    private static readonly (string Path, string Name, CleanupRisk Risk)[] KnownCaches =
    [
        (@"Microsoft\Windows\INetCache",            "IE / Edge Internet Cache",      CleanupRisk.Safe),
        (@"Microsoft\Windows\WebCache",             "Windows Web Cache",             CleanupRisk.Low),
        (@"Google\Chrome\User Data\Default\Cache",  "Google Chrome Cache",           CleanupRisk.Safe),
        (@"Microsoft\Edge\User Data\Default\Cache", "Microsoft Edge Cache",          CleanupRisk.Safe),
        (@"Mozilla\Firefox\Profiles",               "Firefox Profiles Cache",        CleanupRisk.Low),
        (@"Temp",                                   "Local AppData Temp",            CleanupRisk.Low),
        (@"npm-cache",                              "npm Cache",                     CleanupRisk.Safe),
        (@"pip\Cache",                              "pip Cache",                     CleanupRisk.Safe),
        (@"NuGet\Cache",                            "NuGet Package Cache",           CleanupRisk.Safe),
        (@"Yarn\Cache",                             "Yarn Cache",                    CleanupRisk.Safe),
    ];

    public CacheFolderCleanupRule(IScanRepository repo) => _repo = repo;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folders = await _repo.GetLargestFoldersAsync(sessionId, topN: 10_000, cancellationToken);

        var foldersByPath = folders.ToDictionary(
            f => f.FullPath.TrimEnd('\\', '/'),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (subPath, name, risk) in KnownCaches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(localAppData, subPath);

            if (!foldersByPath.TryGetValue(fullPath, out var folder))
                continue;

            if (folder.TotalSizeBytes <= 0)
                continue;

            yield return new CleanupSuggestion
            {
                Id             = Guid.NewGuid(),
                RuleId         = RuleId,
                Title          = $"{name} ({FormatBytes(folder.TotalSizeBytes)})",
                Description    = $"Cache folder at {fullPath}. " +
                                 $"Contains {folder.FileCount:N0} files totalling {FormatBytes(folder.TotalSizeBytes)}.",
                Category       = Category,
                Risk           = risk,
                EstimatedBytes = folder.TotalSizeBytes,
                TargetPaths    = [fullPath],
                IsSystemPath   = false,
            };
        }
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
