namespace StorageMaster.Core.Models;

public enum ScanStatus { Running, Completed, Cancelled, Failed }

/// <summary>Represents one scan run — the root object for all scan data.</summary>
public sealed record ScanSession
{
    public required long   Id            { get; init; }
    public required string RootPath      { get; init; }
    public required DateTime StartedUtc  { get; init; }
    public DateTime? CompletedUtc        { get; init; }
    public required ScanStatus Status    { get; init; }
    public long TotalSizeBytes           { get; init; }
    public long TotalFiles               { get; init; }
    public long TotalFolders             { get; init; }
    public long AccessDeniedCount        { get; init; }
    public string? ErrorMessage          { get; init; }

    public TimeSpan? Duration =>
        CompletedUtc.HasValue ? CompletedUtc.Value - StartedUtc : null;
}
