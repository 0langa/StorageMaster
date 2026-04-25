namespace StorageMaster.Core.Models;

/// <summary>Snapshot of scan progress, emitted periodically on the IProgress callback.</summary>
public sealed record ScanProgress
{
    public required string  CurrentPath    { get; init; }
    public required long    FilesScanned   { get; init; }
    public required long    FoldersScanned { get; init; }
    public required long    BytesScanned   { get; init; }
    public required int     ErrorCount     { get; init; }
    public required bool    IsComplete     { get; init; }
    public DateTime         Timestamp      { get; init; } = DateTime.UtcNow;
}
