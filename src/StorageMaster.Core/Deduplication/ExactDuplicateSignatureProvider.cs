using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

/// <summary>
/// Legacy <see cref="IDuplicateSignatureProvider"/> kept for backward compatibility.
/// New code should use <see cref="ExactSha256Strategy"/> via <see cref="IDuplicateDetectionStrategy"/>.
/// </summary>
public sealed class ExactDuplicateSignatureProvider(IFileContentHasher hasher) : IDuplicateSignatureProvider
{
    public DuplicateMethod Method => DuplicateMethod.ExactSha256;

    public async Task<DuplicateSignature> ComputeAsync(
        DuplicateCandidate candidate,
        CancellationToken  ct = default)
    {
        var hash = await hasher.ComputeSha256Async(candidate.File.FullPath, ct);
        return new DuplicateSignature
        {
            Id               = 0,
            SessionId        = candidate.File.SessionId,
            FileEntryId      = candidate.File.Id,
            Method           = Method,
            Algorithm        = "SHA-256",
            AlgorithmVersion = 1,
            SignatureText    = hash,
            ComputedUtc      = DateTime.UtcNow,
            Status           = "Ready",
            SourceSizeBytes  = candidate.File.SizeBytes,
            SourceModifiedUtc = candidate.File.ModifiedUtc,
        };
    }
}
