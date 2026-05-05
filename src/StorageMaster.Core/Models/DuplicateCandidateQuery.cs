namespace StorageMaster.Core.Models;

/// <summary>
/// Parameters for a single candidate-file enumeration pass, tailored to one detection method.
/// Built by <see cref="IDuplicateDetectionStrategy.BuildCandidateQuery"/> so that each strategy
/// gets exactly the candidate pool it needs — no more, no less.
/// </summary>
public sealed record DuplicateCandidateQuery
{
    public required long SessionId { get; init; }

    public long MinimumSizeBytes { get; init; }

    /// <summary>
    /// When true (for ExactSha256) the SQL subquery restricts candidates to sizes
    /// that appear more than once — a free pre-filter that is valid only for byte-exact
    /// methods. Must be false for any fuzzy/normalized method whose normalized output
    /// can differ in byte size from the original file.
    /// </summary>
    public bool RequireSameSizeBucket { get; init; } = true;

    /// <summary>
    /// Restrict to these lower-case dotted extensions, e.g. ".txt".
    /// Empty = no extension filter.
    /// </summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>
    /// Restrict to these file-type categories.
    /// Empty = all categories.
    /// </summary>
    public IReadOnlyList<FileTypeCategory> Categories { get; init; } = [];

    /// <summary>
    /// Optional path prefixes that define the candidate scope.
    /// Empty = whole scan session.
    /// </summary>
    public IReadOnlyList<string> IncludedPaths { get; init; } = [];

    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];

    public bool IncludeReparsePoints { get; init; }

    public bool IncludeHiddenFiles { get; init; }
}
