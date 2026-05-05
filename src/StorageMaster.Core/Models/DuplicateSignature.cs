namespace StorageMaster.Core.Models;

public sealed record DuplicateSignature
{
    public required long   Id          { get; init; }
    public required long   SessionId   { get; init; }
    public required long   FileEntryId { get; init; }
    public required DuplicateMethod Method    { get; init; }
    public required string Algorithm  { get; init; }

    /// <summary>Integer version of the algorithm. Increment when algorithm changes produce different hashes.</summary>
    public int AlgorithmVersion { get; init; } = 1;

    public byte[]? SignatureBlob { get; init; }
    public string? SignatureText { get; init; }
    public string? MetadataJson  { get; init; }
    public required DateTime ComputedUtc { get; init; }
    public required string   Status      { get; init; }
    public string?           ErrorMessage { get; init; }

    // ── Cache validity metadata ──────────────────────────────────────────────
    /// <summary>File size at computation time. Used to detect stale cached signatures.</summary>
    public long SourceSizeBytes { get; init; }

    /// <summary>File last-write UTC at computation time.</summary>
    public DateTime SourceModifiedUtc { get; init; }

    /// <summary>NTFS file-identity key (volume:fileIndex) at computation time.</summary>
    public string? SourceFileIdentity { get; init; }
}
