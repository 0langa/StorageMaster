namespace StorageMaster.Core.Models;

/// <summary>Controls how a scan is executed.</summary>
public sealed class ScanOptions
{
    public string RootPath { get; init; } = string.Empty;

    /// <summary>Max folders processed concurrently. 1 = sequential (good for HDDs).</summary>
    public int MaxParallelism { get; init; } = 4;

    /// <summary>Flush file entries to the database every N files to bound memory use.</summary>
    public int DbBatchSize { get; init; } = 500;

    /// <summary>Paths to skip entirely (case-insensitive prefix match).</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = DefaultExcludedPaths;

    public bool FollowSymlinks { get; init; } = false;
    public bool IncludeHiddenFiles { get; init; } = false;

    /// <summary>
    /// When true, attempts protected directories, clears the default exclusion
    /// list, and records access-denied errors instead of skipping them silently.
    /// </summary>
    public bool DeepScan { get; init; } = false;

    public static readonly IReadOnlyList<string> DefaultExcludedPaths =
    [
        @"C:\Windows\WinSxS",
        @"C:\Windows\Installer",
    ];
}
