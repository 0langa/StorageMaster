namespace StorageMaster.Core.Models;

public sealed record DuplicateScanOptions
{
    public required long SessionId { get; init; }
    public long MinimumSizeBytes { get; init; } = 1024 * 1024;
    public IReadOnlyList<DuplicateMethod> Methods { get; init; } = [DuplicateMethod.ExactSha256];
    public IReadOnlyList<string> IncludeExtensions { get; init; } = [];
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
    public int MaxConcurrency { get; init; } = 4;
    public KeeperPolicy KeeperPolicy { get; init; } = KeeperPolicy.Newest;
    public bool IncludeReparsePoints { get; init; }
}
