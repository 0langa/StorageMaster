using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scanner;

public static class ScanOptionValidator
{
    public const int DefaultMaxParallelism = 4;
    public const int DefaultDbBatchSize = 500;
    public const int MinimumDbBatchSize = 10;
    public const int MaximumDbBatchSize = 10_000;

    public static ScanOptions NormalizeAndValidate(ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.RootPath))
            throw new ArgumentException("Scan root path is required.", nameof(options));

        var rootPath = NormalizeDirectoryPath(options.RootPath);
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Scan root does not exist: {rootPath}");

        var maxParallelism = options.MaxParallelism <= 0
            ? DefaultMaxParallelism
            : Math.Clamp(options.MaxParallelism, 1, Math.Max(1, Environment.ProcessorCount * 2));

        var dbBatchSize = options.DbBatchSize <= 0
            ? DefaultDbBatchSize
            : Math.Clamp(options.DbBatchSize, MinimumDbBatchSize, MaximumDbBatchSize);

        var exclusions = options.DeepScan
            ? []
            : NormalizeExcludedPaths(options.ExcludedPaths);

        return new ScanOptions
        {
            RootPath = rootPath,
            MaxParallelism = maxParallelism,
            DbBatchSize = dbBatchSize,
            ExcludedPaths = exclusions,
            FollowSymlinks = options.FollowSymlinks,
            IncludeHiddenFiles = options.IncludeHiddenFiles,
            DeepScan = options.DeepScan,
        };
    }

    public static IReadOnlyList<string> NormalizeExcludedPaths(IEnumerable<string> paths)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            try
            {
                normalized.Add(NormalizeDirectoryPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // User settings can contain stale or malformed paths. Skip them
                // instead of making every scan fail before it starts.
            }
        }

        return normalized
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string NormalizePathForStorage(string path) =>
        NormalizeDirectoryPath(path).ToUpperInvariant();

    /// <summary>
    /// Returns the parent of an already storage-normalised path, or <c>null</c>
    /// when the path is a root and has no parent inside the scan.
    /// <para>
    /// This is a pure substring operation on purpose. The input has already been
    /// upper-cased by <see cref="NormalizePathForStorage"/>, and re-normalising
    /// here would risk disagreeing with the schema v13 SQL backfill, whose
    /// SQLite <c>upper()</c> is ASCII-only. Keep this method and the
    /// <c>V13Statements</c> expression behaviourally identical.
    /// </para>
    /// </summary>
    public static string? GetParentOfNormalizedPath(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return null;

        var lastSeparator = normalizedPath.LastIndexOf(Path.DirectorySeparatorChar);
        if (lastSeparator < 0)
            return null;

        // Trailing separator means the value is already a root such as "C:\".
        if (lastSeparator == normalizedPath.Length - 1)
            return null;

        // Keep the separator for a drive root so "C:\USERS" reports "C:\", not "C:".
        var parent = lastSeparator == 2 && normalizedPath[1] == ':'
            ? normalizedPath[..(lastSeparator + 1)]
            : normalizedPath[..lastSeparator];

        return parent.Length == 0 ? null : parent;
    }

    public static string GetDisplayName(string path)
    {
        var normalized = NormalizeDirectoryPath(path);
        var name = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(name) ? normalized : name;
    }

    public static bool IsPathEqualOrUnder(string candidatePath, string ancestorPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(ancestorPath))
            return false;

        var candidate = NormalizeDirectoryPath(candidatePath);
        var ancestor = NormalizeDirectoryPath(ancestorPath);

        if (string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!candidate.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase))
            return false;

        // A normalized root already ends with a directory separator, so the
        // successful prefix match above establishes the path boundary.
        if (Path.EndsInDirectorySeparator(ancestor))
            return true;

        var boundaryIndex = ancestor.Length;
        return candidate.Length > boundaryIndex &&
               (candidate[boundaryIndex] == Path.DirectorySeparatorChar ||
                candidate[boundaryIndex] == Path.AltDirectorySeparatorChar);
    }

    public static bool IsExcluded(string path, IEnumerable<string> excludedPaths) =>
        excludedPaths.Any(excluded => IsPathEqualOrUnder(path, excluded));
}
