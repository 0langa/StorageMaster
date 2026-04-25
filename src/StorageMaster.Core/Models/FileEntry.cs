namespace StorageMaster.Core.Models;

/// <summary>Immutable snapshot of a single file discovered during a scan.</summary>
public sealed record FileEntry
{
    public required long   Id            { get; init; }
    public required long   SessionId     { get; init; }
    public required string FullPath      { get; init; }
    public required string FileName      { get; init; }
    public required string Extension     { get; init; }
    public required long   SizeBytes     { get; init; }
    public required DateTime CreatedUtc  { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required DateTime AccessedUtc { get; init; }
    public required FileAttributes Attributes { get; init; }
    public required FileTypeCategory Category   { get; init; }

    /// <summary>True when this entry was reachable only via a reparse point (symlink/junction).</summary>
    public bool IsReparsePoint { get; init; }

    public string ParentPath => Path.GetDirectoryName(FullPath) ?? string.Empty;
}
