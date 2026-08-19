using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
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

    public string RuleId => "core.cache-folders";
    public string DisplayName => Loc.Get("Rule_ApplicationCaches_Name");
    public CleanupCategory Category => CleanupCategory.CacheFolders;

    // (subpath-under-LocalAppData, display-name key, risk)
    // The name is held as a catalogue key rather than a resolved string: this
    // array is initialised once per process, so resolving here would freeze the
    // folder names in whatever language happened to be active at first use.
    // Note: do NOT add %LocalAppData%\Temp here — TempFilesCleanupRule already
    // covers it at the individual-file level. Adding the folder here would cause
    // "file not found" failures on the temp-file suggestion when both are selected.
    // For the same reason the Chromium "…\User Data\Default\Cache" folders are absent:
    // BrowserCacheCleanupRule already yields the cache of every Chrome and Edge
    // profile, so listing the Default profile here as well produced two auto-selected
    // suggestions for one directory and double-counted it in the estimated savings
    // shown before the user confirms.
    private static readonly (string Path, string NameKey, CleanupRisk Risk)[] KnownCaches =
    [
        (@"Microsoft\Windows\INetCache",            "Rule_ApplicationCaches_InternetCache",   CleanupRisk.Safe),
        (@"Microsoft\Windows\WebCache",             "Rule_ApplicationCaches_WebCache",        CleanupRisk.Low),
        (@"Mozilla\Firefox\Profiles",               "Rule_ApplicationCaches_FirefoxProfiles", CleanupRisk.Low),
        (@"npm-cache",                              "Rule_ApplicationCaches_Npm",             CleanupRisk.Safe),
        (@"pip\Cache",                              "Rule_ApplicationCaches_Pip",             CleanupRisk.Safe),
        (@"NuGet\Cache",                            "Rule_ApplicationCaches_NuGet",           CleanupRisk.Safe),
        (@"Yarn\Cache",                             "Rule_ApplicationCaches_Yarn",            CleanupRisk.Safe),
    ];

    public CacheFolderCleanupRule(IScanRepository repo) => _repo = repo;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folders = await _repo.GetLargestFoldersAsync(sessionId, topN: 10_000, cancellationToken);

        var foldersByPath = folders.ToDictionary(
            f => f.FullPath.TrimEnd('\\', '/'),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (subPath, nameKey, risk) in KnownCaches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(localAppData, subPath);

            if (!foldersByPath.TryGetValue(fullPath, out var folder))
                continue;

            if (folder.TotalSizeBytes <= 0)
                continue;

            yield return new CleanupSuggestion
            {
                Id = Guid.NewGuid(),
                RuleId = RuleId,
                Title = Loc.Format(
                    "Rule_ApplicationCaches_Title",
                    Loc.Get(nameKey),
                    FormatBytes(folder.TotalSizeBytes)),
                Description = Loc.Format(
                    "Rule_ApplicationCaches_Description",
                    fullPath,
                    folder.FileCount.ToString("N0"),
                    FormatBytes(folder.TotalSizeBytes)),
                Category = Category,
                Risk = risk,
                EstimatedBytes = folder.TotalSizeBytes,
                TargetPaths = [fullPath],
                IsSystemPath = false,
            };
        }
    }

    private static string FormatBytes(long bytes) => ByteFormat.Format(bytes);
}
