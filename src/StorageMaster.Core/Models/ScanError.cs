namespace StorageMaster.Core.Models;

public sealed record ScanError
{
    public required long   Id         { get; init; }
    public required long   SessionId  { get; init; }
    public required string Path       { get; init; }
    public required string ErrorType  { get; init; }
    public required string Message    { get; init; }
    public required DateTime OccurredAt { get; init; }
}
