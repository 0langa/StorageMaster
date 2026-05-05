namespace StorageMaster.Core.Models;

public sealed record DuplicateSignature
{
    public required long Id { get; init; }
    public required long SessionId { get; init; }
    public required long FileEntryId { get; init; }
    public required DuplicateMethod Method { get; init; }
    public required string Algorithm { get; init; }
    public byte[]? SignatureBlob { get; init; }
    public string? SignatureText { get; init; }
    public string? MetadataJson { get; init; }
    public required DateTime ComputedUtc { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
}
