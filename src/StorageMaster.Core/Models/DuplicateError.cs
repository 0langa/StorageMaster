namespace StorageMaster.Core.Models;

public sealed record DuplicateError
{
    public required long Id { get; init; }
    public required long RunId { get; init; }
    public long? FileEntryId { get; init; }
    public required string Path { get; init; }
    public required string ErrorType { get; init; }
    public required string Message { get; init; }
    public required DateTime OccurredUtc { get; init; }
}
