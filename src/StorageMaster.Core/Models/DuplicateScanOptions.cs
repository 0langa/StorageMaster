namespace StorageMaster.Core.Models;

public sealed record DuplicateScanOptions
{
    public required long SessionId { get; init; }

    /// <summary>Minimum file size in bytes to include in candidate enumeration.</summary>
    public long MinimumSizeBytes { get; init; } = 1024 * 1024;

    public IReadOnlyList<DuplicateMethod> Methods { get; init; } = [DuplicateMethod.ExactSha256];

    /// <summary>
    /// Restrict to these file extensions (lower-case with leading dot, e.g. ".jpg").
    /// Empty list = all extensions.
    /// </summary>
    public IReadOnlyList<string> IncludeExtensions { get; init; } = [];

    /// <summary>
    /// Restrict to these file-type categories.
    /// Empty list = all categories.
    /// </summary>
    public IReadOnlyList<FileTypeCategory> IncludeCategories { get; init; } = [];

    /// <summary>
    /// Optional path prefixes that define the dedupe scope inside the session.
    /// Empty list = whole scan session.
    /// </summary>
    public IReadOnlyList<string> IncludedPaths { get; init; } = [];

    /// <summary>Paths (prefix match) to exclude from candidate enumeration.</summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];

    /// <summary>Maximum parallel I/O operations. Should respect AppSettings.ScanParallelism.</summary>
    public int MaxConcurrency { get; init; } = 4;

    public KeeperPolicy KeeperPolicy { get; init; } = KeeperPolicy.Newest;

    /// <summary>When false (default), reparse points (junctions/symlinks) are excluded.</summary>
    public bool IncludeReparsePoints { get; init; }

    /// <summary>Include files with Hidden or System attributes.</summary>
    public bool IncludeHiddenFiles { get; init; }
}
