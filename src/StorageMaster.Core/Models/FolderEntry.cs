namespace StorageMaster.Core.Models;

/// <summary>Aggregated size information for a scanned directory.</summary>
public sealed record FolderEntry
{
    public required long   Id              { get; init; }
    public required long   SessionId       { get; init; }
    public required string FullPath        { get; init; }
    public required string FolderName      { get; init; }
    public required long   DirectSizeBytes { get; init; }
    public required long   TotalSizeBytes  { get; init; }
    public required int    FileCount       { get; init; }
    public required int    SubFolderCount  { get; init; }
    public required bool   IsReparsePoint  { get; init; }
    public required bool   WasAccessDenied { get; init; }

    public string? ParentPath => Path.GetDirectoryName(FullPath);
}
