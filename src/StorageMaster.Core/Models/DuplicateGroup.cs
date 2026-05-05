namespace StorageMaster.Core.Models;

public sealed record DuplicateGroup
{
    public required long Id { get; init; }
    public required long RunId { get; init; }
    public required DuplicateMethod Method { get; init; }
    public required string Algorithm { get; init; }
    public required double Confidence { get; init; }
    public required long TotalBytes { get; init; }
    public required long ReclaimableBytes { get; init; }
    public required long RepresentativeFileEntryId { get; init; }
}
