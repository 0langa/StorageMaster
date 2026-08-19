using System.Security;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.SmartCleaner;

/// <summary>
/// Implements the Smart Cleaner workflow.
/// Scans seven well-known junk locations directly (no scan database required)
/// and deletes items via IFileDeleter.
///
/// Safe-deletion policy:
///   • Only locations with CleanupRisk ≤ Low are included automatically.
///   • Nothing in Program Files, Windows, or System32 is ever touched.
///   • Every deletion target is an analysis-time file beneath an allow-listed root.
/// </summary>
public sealed class SmartCleanerService : ISmartCleanerService
{
    private readonly IFileDeleter _deleter;
    private readonly INoFollowFileEnumerator _fileEnumerator;
    private readonly ICleanupLogRepository _log;
    private readonly ILogger<SmartCleanerService> _logger;

    public SmartCleanerService(
        IFileDeleter deleter,
        INoFollowFileEnumerator fileEnumerator,
        ICleanupLogRepository log,
        ILogger<SmartCleanerService> logger)
    {
        _deleter = deleter;
        _fileEnumerator = fileEnumerator;
        _log = log;
        _logger = logger;
    }

    // Maps Smart Cleaner category strings to the shared CleanupCategory enum so that
    // CleanupLog entries are consistent with those produced by the rule-based engine.
    private static CleanupCategory MapCategory(string category) => category switch
    {
        "Temporary Files" => CleanupCategory.TempFiles,
        "Browser Cache" => CleanupCategory.BrowserCache,
        "Windows Update Cache" => CleanupCategory.WindowsUpdateCache,
        "Error Reports & Crash Dumps" => CleanupCategory.WindowsErrorReporting,
        "Delivery Optimization Cache" => CleanupCategory.DeliveryOptimization,
        _ => CleanupCategory.CacheFolders,
    };

    public async Task<SmartCleanAnalysisResult> AnalyzeAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var groups = new List<SmartCleanGroup>();
        var warnings = new List<NoFollowFileEnumerationError>();

        progress?.Report("Scanning temporary files…");
        var tempFiles = new List<string>();
        var tempSnapshots = CreateSnapshotMap();
        long tempBytes = 0;
        foreach (var root in GetTempRoots())
        {
            tempBytes = checked(tempBytes + await CollectLocationAsync(
                new SmartCleanScanLocation(root, root),
                tempFiles,
                tempSnapshots,
                warnings,
                predicate: null,
                ct).ConfigureAwait(false));
        }
        AddGroupIfNotEmpty(
            SmartCleanSource.TemporaryFiles,
            "Temporary Files",
            "Files in Windows temp folders that are safe to delete.",
            "",
            tempBytes,
            tempFiles,
            tempSnapshots);

        progress?.Report("Scanning browser caches…");
        var browserFiles = new List<string>();
        var browserSnapshots = CreateSnapshotMap();
        long browserBytes = 0;
        foreach (var location in GetBrowserCacheLocations())
        {
            browserBytes = checked(browserBytes + await CollectLocationAsync(
                location,
                browserFiles,
                browserSnapshots,
                warnings,
                predicate: null,
                ct).ConfigureAwait(false));
        }
        AddGroupIfNotEmpty(
            SmartCleanSource.BrowserCache,
            "Browser Cache",
            "Cached web content from Chrome, Edge, Firefox, Brave, and Opera.",
            "",
            browserBytes,
            browserFiles,
            browserSnapshots);

        progress?.Report("Scanning Windows Update cache…");
        if (TryGetKnownFolderChild(
                Environment.SpecialFolder.Windows,
                ["SoftwareDistribution", "Download"],
                out var windowsUpdateRoot))
        {
            var files = new List<string>();
            var snapshots = CreateSnapshotMap();
            var bytes = await CollectLocationAsync(
                new SmartCleanScanLocation(windowsUpdateRoot, windowsUpdateRoot),
                files,
                snapshots,
                warnings,
                predicate: null,
                ct).ConfigureAwait(false);
            AddGroupIfNotEmpty(
                SmartCleanSource.WindowsUpdateCache,
                "Windows Update Cache",
                "Downloaded update packages already applied. Windows re-downloads as needed.",
                "",
                bytes,
                files,
                snapshots);
        }

        progress?.Report("Scanning error reports…");
        var werFiles = new List<string>();
        var werSnapshots = CreateSnapshotMap();
        long werBytes = 0;
        foreach (var root in GetWerDirectoryRoots())
        {
            werBytes = checked(werBytes + await CollectLocationAsync(
                new SmartCleanScanLocation(root, root),
                werFiles,
                werSnapshots,
                warnings,
                predicate: null,
                ct).ConfigureAwait(false));
        }
        AddGroupIfNotEmpty(
            SmartCleanSource.WindowsErrorReporting,
            "Error Reports & Crash Dumps",
            "Windows diagnostic files from app crashes. Already sent to Microsoft if you opted in.",
            "",
            werBytes,
            werFiles,
            werSnapshots);

        progress?.Report("Scanning Delivery Optimization cache…");
        if (TryGetKnownFolderChild(
                Environment.SpecialFolder.Windows,
                ["SoftwareDistribution", "DeliveryOptimization"],
                out var deliveryRoot))
        {
            var files = new List<string>();
            var snapshots = CreateSnapshotMap();
            var bytes = await CollectLocationAsync(
                new SmartCleanScanLocation(deliveryRoot, deliveryRoot),
                files,
                snapshots,
                warnings,
                predicate: null,
                ct).ConfigureAwait(false);
            AddGroupIfNotEmpty(
                SmartCleanSource.DeliveryOptimizationCache,
                "Delivery Optimization Cache",
                "Peer-to-peer Windows Update sharing cache. Rebuilds automatically.",
                "",
                bytes,
                files,
                snapshots);
        }

        progress?.Report("Scanning thumbnail cache…");
        if (TryGetKnownFolderChild(
                Environment.SpecialFolder.LocalApplicationData,
                ["Microsoft", "Windows", "Explorer"],
                out var thumbnailRoot))
        {
            var files = new List<string>();
            var snapshots = CreateSnapshotMap();
            var bytes = await CollectLocationAsync(
                new SmartCleanScanLocation(thumbnailRoot, thumbnailRoot),
                files,
                snapshots,
                warnings,
                snapshot => IsThumbnailTargetUnder(snapshot.Path, thumbnailRoot),
                ct).ConfigureAwait(false);
            AddGroupIfNotEmpty(
                SmartCleanSource.ThumbnailCache,
                "Thumbnail Cache",
                "Cached image previews for Windows Explorer. Rebuilt automatically when you browse folders.",
                "",
                bytes,
                files,
                snapshots);
        }

        progress?.Report("Scanning shader cache…");
        if (TryGetKnownFolderChild(
                Environment.SpecialFolder.LocalApplicationData,
                ["D3DSCache"],
                out var shaderRoot))
        {
            var files = new List<string>();
            var snapshots = CreateSnapshotMap();
            var bytes = await CollectLocationAsync(
                new SmartCleanScanLocation(shaderRoot, shaderRoot),
                files,
                snapshots,
                warnings,
                predicate: null,
                ct).ConfigureAwait(false);
            AddGroupIfNotEmpty(
                SmartCleanSource.DirectXShaderCache,
                "DirectX Shader Cache",
                "Compiled GPU shader programs. Rebuilt by games and apps on next launch.",
                "",
                bytes,
                files,
                snapshots);
        }

        return new SmartCleanAnalysisResult(groups.ToArray(), warnings.ToArray());

        void AddGroupIfNotEmpty(
            SmartCleanSource source,
            string category,
            string description,
            string iconGlyph,
            long bytes,
            List<string> paths,
            Dictionary<string, FileSnapshot> snapshots)
        {
            if (bytes > 0)
                groups.Add(new SmartCleanGroup(
                    source,
                    category,
                    description,
                    iconGlyph,
                    bytes,
                    paths.ToArray(),
                    snapshots));
        }
    }

    public async Task<SmartCleanResult> CleanAsync(
        IReadOnlyList<SmartCleanGroup> groups,
        DeletionMethod method,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        long totalFreed = 0;
        long totalProcessed = 0;
        var successfulPathCount = 0;
        var allFailures = new List<SmartCleanFailure>();
        var auditWarnings = new List<string>();

        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            progress?.Report($"Cleaning {group.Category}…");

            var distinctPaths = group.Paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var failedPaths = new List<string>();
            var reportedFailurePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unattemptedPaths = new HashSet<string>(distinctPaths, StringComparer.OrdinalIgnoreCase);
            string? firstError = null;
            long groupFreed = 0;
            long groupProcessed = 0;
            var groupSuccessfulPathCount = 0;
            string? inFlightPath = null;

            try
            {
                ct.ThrowIfCancellationRequested();

                // The allow-listed roots are fixed for the whole group, so resolve them
                // once here instead of rebuilding them for every path (see
                // SmartCleanBoundaryResolver for what that cost on browser caches).
                var boundaryResolver = SmartCleanBoundaryResolver.Create(group.Source);

                foreach (var path in distinctPaths)
                {
                    ct.ThrowIfCancellationRequested();

                    if (method is not (DeletionMethod.RecycleBin or DeletionMethod.Permanent))
                    {
                        RejectPath(path, "Smart Cleaner supports only Recycle Bin or permanent deletion.");
                        continue;
                    }

                    if (!boundaryResolver.TryResolveAllowedBoundary(path, out var boundaryRoot))
                    {
                        RejectPath(path, "Path is outside the selected Smart Cleaner source boundary.");
                        continue;
                    }

                    if (!group.ExpectedFileSnapshots.TryGetValue(path, out var expectedSnapshot) ||
                        !PathsEqual(path, expectedSnapshot.Path))
                    {
                        RejectPath(path, "No matching analysis-time file snapshot is available.");
                        continue;
                    }

                    using var validationLease = await _fileEnumerator
                        .TryOpenValidatedFileAsync(boundaryRoot, expectedSnapshot, ct)
                        .ConfigureAwait(false);
                    if (validationLease is null)
                    {
                        RejectPath(
                            path,
                            "File or its directory ancestry changed, was replaced, disappeared, became inaccessible, or lacks a stable identity after analysis.");
                        continue;
                    }

                    var liveSnapshot = validationLease.LiveSnapshot;
                    inFlightPath = path;
                    var outcome = await _deleter.DeleteAsync(new DeletionRequest(
                        path,
                        method,
                        DryRun: false,
                        ExpectedSnapshot: liveSnapshot), ct).ConfigureAwait(false);
                    inFlightPath = null;
                    unattemptedPaths.Remove(path);
                    if (outcome.Success)
                    {
                        checked
                        {
                            groupProcessed += liveSnapshot.SizeBytes;
                            if (method == DeletionMethod.Permanent)
                            {
                                groupFreed += Math.Clamp(
                                    outcome.BytesFreed,
                                    0,
                                    liveSnapshot.SizeBytes);
                            }
                        }
                        groupSuccessfulPathCount++;
                        successfulPathCount++;
                        _logger.LogInformation(
                            "[SmartCleaner] Processed {Size} bytes at {Path} using {Method}",
                            liveSnapshot.SizeBytes,
                            path,
                            method);
                    }
                    else
                    {
                        AddFailure(path, outcome.Error ?? "Deletion failed.");
                        _logger.LogWarning(
                            "[SmartCleaner] Failed {Path}: {Error}",
                            path,
                            outcome.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var path in unattemptedPaths)
                {
                    AddFailure(
                        path,
                        inFlightPath is not null && PathsEqual(path, inFlightPath)
                            ? $"Cleanup stopped before a confirmed deletion outcome; rescan to determine this path's state: {ex.Message}"
                            : $"Not attempted because cleanup was interrupted: {ex.Message}");
                }

                for (var remainingIndex = groupIndex + 1; remainingIndex < groups.Count; remainingIndex++)
                {
                    foreach (var path in groups[remainingIndex].Paths.Distinct(StringComparer.OrdinalIgnoreCase))
                        allFailures.Add(new SmartCleanFailure(path, "Not attempted because cleanup was interrupted."));
                }

                totalFreed += groupFreed;
                totalProcessed += groupProcessed;
                await WriteAuditAsync(
                        group,
                        groupSuccessfulPathCount,
                        groupFreed,
                        failedPaths,
                        firstError,
                        auditWarnings)
                    .ConfigureAwait(false);

                return new SmartCleanResult(
                    totalFreed,
                    totalProcessed,
                    successfulPathCount,
                    allFailures,
                    auditWarnings,
                    WasCancelled: ex is OperationCanceledException,
                    ErrorMessage: ex is OperationCanceledException ? null : ex.Message);
            }

            totalFreed += groupFreed;
            totalProcessed += groupProcessed;
            await WriteAuditAsync(
                    group,
                    groupSuccessfulPathCount,
                    groupFreed,
                    failedPaths,
                    firstError,
                    auditWarnings)
                .ConfigureAwait(false);

            void AddFailure(string path, string error)
            {
                if (!reportedFailurePaths.Add(path))
                    return;

                failedPaths.Add(path);
                firstError ??= error;
                allFailures.Add(new SmartCleanFailure(path, error));
            }

            void RejectPath(string path, string error)
            {
                unattemptedPaths.Remove(path);
                AddFailure(path, error);
            }
        }

        return new SmartCleanResult(
            totalFreed,
            totalProcessed,
            successfulPathCount,
            allFailures,
            auditWarnings);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task WriteAuditAsync(
        SmartCleanGroup group,
        int successfulPathCount,
        long bytesFreed,
        IReadOnlyList<string> failedPaths,
        string? firstError,
        List<string> auditWarnings)
    {
        var suggestionId = Guid.NewGuid();
        var distinctTargetCount = group.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var syntheticSuggestion = new CleanupSuggestion
        {
            Id = suggestionId,
            RuleId = $"smart-cleaner.{group.Source.ToString().ToLowerInvariant()}",
            Title = group.Category,
            Description = group.Description,
            Category = MapCategory(group.Category),
            Risk = CleanupRisk.Low,
            EstimatedBytes = group.EstimatedBytes,
            TargetPaths = group.Paths,
            ExpectedFileSnapshots = group.ExpectedFileSnapshots,
        };
        var syntheticResult = new CleanupResult
        {
            SuggestionId = suggestionId,
            Status = failedPaths.Count == 0 && successfulPathCount == distinctTargetCount && distinctTargetCount > 0
                ? CleanupResultStatus.Success
                : successfulPathCount > 0
                    ? CleanupResultStatus.PartialSuccess
                    : distinctTargetCount > 0
                        ? CleanupResultStatus.Failed
                        : CleanupResultStatus.Skipped,
            BytesFreed = bytesFreed,
            ExecutedUtc = DateTime.UtcNow,
            WasDryRun = false,
            FailedPaths = failedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ErrorMessage = firstError,
        };

        try
        {
            await _log.LogResultAsync(syntheticResult, syntheticSuggestion, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var warning = $"Could not write cleanup audit for {group.Category}: {ex.Message}";
            auditWarnings.Add(warning);
            _logger.LogWarning(ex, "[SmartCleaner] Failed to write audit log for {Category}", group.Category);
        }
    }

    private async Task<long> CollectLocationAsync(
        SmartCleanScanLocation location,
        List<string> paths,
        IDictionary<string, FileSnapshot> snapshots,
        List<NoFollowFileEnumerationError> warnings,
        Func<FileSnapshot, bool>? predicate,
        CancellationToken ct)
    {
        if (!Directory.Exists(location.ScanRoot))
            return 0;

        var result = await _fileEnumerator
            .EnumerateAsync(location.BoundaryRoot, location.ScanRoot, ct)
            .ConfigureAwait(false);
        warnings.AddRange(result.Errors);

        long bytes = 0;
        foreach (var snapshot in result.Files)
        {
            ct.ThrowIfCancellationRequested();
            if ((predicate is not null && !predicate(snapshot)) ||
                !snapshots.TryAdd(snapshot.Path, snapshot))
            {
                continue;
            }

            checked { bytes += snapshot.SizeBytes; }
            paths.Add(snapshot.Path);
        }

        return bytes;
    }

    private static Dictionary<string, FileSnapshot> CreateSnapshotMap() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The allow-listed deletion boundaries of one <see cref="SmartCleanSource"/>,
    /// resolved once per clean operation rather than once per path.
    ///
    /// The roots are fixed for the whole run — analysis resolves them exactly once —
    /// but the per-path check used to rebuild them for every single file. On
    /// <see cref="SmartCleanSource.BrowserCache"/> that meant re-enumerating every
    /// Chromium and Firefox profile directory (hundreds of metadata syscalls, tens of
    /// milliseconds) per deleted file, which dominated the entire clean.
    ///
    /// Snapshotting the roots up front is also strictly more conservative than
    /// resolving per path: a cache directory that appears mid-run is never added to
    /// the allow-list, and every accepted path still has to pass the per-file
    /// validated-open lease — which re-checks directory ancestry against the boundary
    /// returned here — before anything is deleted.
    /// </summary>
    private sealed class SmartCleanBoundaryResolver
    {
        private readonly IReadOnlyList<ResolvedBoundary> _boundaries;
        private readonly bool _requireThumbnailName;

        private SmartCleanBoundaryResolver(
            IReadOnlyList<ResolvedBoundary> boundaries,
            bool requireThumbnailName)
        {
            _boundaries = boundaries;
            _requireThumbnailName = requireThumbnailName;
        }

        internal static SmartCleanBoundaryResolver Create(SmartCleanSource source) => source switch
        {
            SmartCleanSource.TemporaryFiles => FromRoots(GetTempRoots()),

            SmartCleanSource.BrowserCache => FromLocations(GetBrowserCacheLocations()),

            SmartCleanSource.WindowsUpdateCache => FromKnownFolder(
                Environment.SpecialFolder.Windows,
                ["SoftwareDistribution", "Download"]),

            SmartCleanSource.WindowsErrorReporting => FromRoots(GetWerDirectoryRoots()),

            SmartCleanSource.DeliveryOptimizationCache => FromKnownFolder(
                Environment.SpecialFolder.Windows,
                ["SoftwareDistribution", "DeliveryOptimization"]),

            // Thumbnail cleaning only ever targets thumbcache_*.db sitting directly in
            // the Explorer folder, so that name/parent check stays per path.
            SmartCleanSource.ThumbnailCache => FromKnownFolder(
                Environment.SpecialFolder.LocalApplicationData,
                ["Microsoft", "Windows", "Explorer"],
                requireThumbnailName: true),

            SmartCleanSource.DirectXShaderCache => FromKnownFolder(
                Environment.SpecialFolder.LocalApplicationData,
                ["D3DSCache"]),

            _ => new SmartCleanBoundaryResolver([], requireThumbnailName: false),
        };

        internal bool TryResolveAllowedBoundary(string path, out string boundaryRoot)
        {
            boundaryRoot = string.Empty;
            if (!TryCanonicalizeAbsolutePath(path, out var canonicalPath) || Directory.Exists(canonicalPath))
                return false;

            foreach (var boundary in _boundaries)
            {
                if (!boundary.IsStrictDescendant(canonicalPath))
                    continue;

                if (_requireThumbnailName && !IsThumbnailTargetUnder(canonicalPath, boundary.ScanRoot))
                    continue;

                boundaryRoot = boundary.BoundaryRoot;
                return true;
            }

            return false;
        }

        private static SmartCleanBoundaryResolver FromRoots(IEnumerable<string> roots) =>
            FromLocations(roots.Select(root => new SmartCleanScanLocation(root, root)));

        private static SmartCleanBoundaryResolver FromKnownFolder(
            Environment.SpecialFolder folder,
            string[] segments,
            bool requireThumbnailName = false) =>
            TryGetKnownFolderChild(folder, segments, out var root)
                ? FromLocations([new SmartCleanScanLocation(root, root)], requireThumbnailName)
                : new SmartCleanBoundaryResolver([], requireThumbnailName);

        private static SmartCleanBoundaryResolver FromLocations(
            IEnumerable<SmartCleanScanLocation> locations,
            bool requireThumbnailName = false)
        {
            var boundaries = new List<ResolvedBoundary>();
            foreach (var location in locations)
            {
                // A root that cannot be canonicalized can never contain a canonical
                // path, so dropping it here fails closed exactly as before.
                if (!TryCanonicalizeAbsolutePath(location.ScanRoot, out var scanRoot))
                    continue;

                boundaries.Add(new ResolvedBoundary(
                    location.BoundaryRoot,
                    scanRoot,
                    scanRoot.EndsWith(Path.DirectorySeparatorChar)
                        ? scanRoot
                        : scanRoot + Path.DirectorySeparatorChar));
            }

            return new SmartCleanBoundaryResolver(boundaries, requireThumbnailName);
        }

        private sealed record ResolvedBoundary(
            string BoundaryRoot,
            string ScanRoot,
            string ScanRootPrefix)
        {
            internal bool IsStrictDescendant(string canonicalPath) =>
                !string.Equals(canonicalPath, ScanRoot, StringComparison.OrdinalIgnoreCase) &&
                canonicalPath.StartsWith(ScanRootPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsThumbnailTargetUnder(string path, string explorerRoot)
    {
        if (!PathsEqual(Path.GetDirectoryName(path) ?? string.Empty, explorerRoot))
            return false;

        var name = Path.GetFileName(path);
        return name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) &&
               name.EndsWith(".db", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        TryCanonicalizeAbsolutePath(left, out var canonicalLeft) &&
        TryCanonicalizeAbsolutePath(right, out var canonicalRight) &&
        string.Equals(canonicalLeft, canonicalRight, StringComparison.OrdinalIgnoreCase);

    private static bool TryCanonicalizeAbsolutePath(string path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            canonicalPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.IsPathFullyQualified(canonicalPath) && !string.IsNullOrWhiteSpace(canonicalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    private static IReadOnlyList<string> GetTempRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.Windows, ["Temp"], out var windowsTemp))
            roots.Add(windowsTemp);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.LocalApplicationData, ["Temp"], out var localTemp))
            roots.Add(localTemp);
        return roots.ToArray();
    }

    private static List<SmartCleanScanLocation> GetBrowserCacheLocations()
    {
        var locations = new List<SmartCleanScanLocation>();
        if (TryGetKnownFolderChild(Environment.SpecialFolder.LocalApplicationData, ["Google", "Chrome", "User Data"], out var chrome))
            AddBrowserCacheDirs(chrome, locations);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.LocalApplicationData, ["Microsoft", "Edge", "User Data"], out var edge))
            AddBrowserCacheDirs(edge, locations);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.LocalApplicationData, ["BraveSoftware", "Brave-Browser", "User Data"], out var brave))
            AddBrowserCacheDirs(brave, locations);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.LocalApplicationData, ["Mozilla", "Firefox", "Profiles"], out var firefoxLocal))
            AddFirefoxCacheDirs(firefoxLocal, locations);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.ApplicationData, ["Mozilla", "Firefox", "Profiles"], out var firefoxRoaming))
            AddFirefoxCacheDirs(firefoxRoaming, locations);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.ApplicationData, ["Opera Software"], out var opera))
            AddBrowserCacheDirs(opera, locations);
        return locations;
    }

    private static void AddBrowserCacheDirs(
        string baseDir,
        List<SmartCleanScanLocation> locations)
    {
        if (!Directory.Exists(baseDir) || IsReparsePoint(baseDir)) return;
        foreach (var profile in EnumerateNormalDirectories(baseDir))
        {
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                var p = Path.Combine(profile, sub);
                if (Directory.Exists(p) && !IsReparsePoint(p) && TryCanonicalizeAbsolutePath(p, out var canonical))
                    locations.Add(new SmartCleanScanLocation(baseDir, canonical));
            }
        }
    }

    private static void AddFirefoxCacheDirs(
        string baseDir,
        List<SmartCleanScanLocation> locations)
    {
        if (!Directory.Exists(baseDir) || IsReparsePoint(baseDir)) return;
        foreach (var profile in EnumerateNormalDirectories(baseDir))
        {
            foreach (var sub in new[] { "cache2", "startupCache" })
            {
                var p = Path.Combine(profile, sub);
                if (Directory.Exists(p) && !IsReparsePoint(p) && TryCanonicalizeAbsolutePath(p, out var canonical))
                    locations.Add(new SmartCleanScanLocation(baseDir, canonical));
            }
        }
    }

    private static IEnumerable<string> EnumerateNormalDirectories(string root)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            if (!IsReparsePoint(directory) && TryCanonicalizeAbsolutePath(directory, out var canonical))
                yield return canonical;
        }
    }

    private static IReadOnlyList<string> GetWerDirectoryRoots()
    {
        var roots = new List<string>();
        if (TryGetKnownFolderChild(Environment.SpecialFolder.LocalApplicationData, ["Microsoft", "Windows", "WER"], out var userWer))
            roots.Add(userWer);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.CommonApplicationData, ["Microsoft", "Windows", "WER"], out var systemWer))
            roots.Add(systemWer);
        if (TryGetKnownFolderChild(Environment.SpecialFolder.LocalApplicationData, ["CrashDumps"], out var crashes))
            roots.Add(crashes);
        return roots;
    }

    private static bool TryGetKnownFolderChild(
        Environment.SpecialFolder folder,
        string[] segments,
        out string path)
    {
        path = string.Empty;
        if (!TryGetKnownFolderRoot(folder, out var root))
            return false;

        try
        {
            return TryCanonicalizeAbsolutePath(Path.Combine([root, .. segments]), out path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetKnownFolderRoot(Environment.SpecialFolder folder, out string path)
    {
        path = string.Empty;
        var raw = Environment.GetFolderPath(folder);
        return !string.IsNullOrWhiteSpace(raw) && TryCanonicalizeAbsolutePath(raw, out path);
    }

    private sealed record SmartCleanScanLocation(string BoundaryRoot, string ScanRoot);
}
