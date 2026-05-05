using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateSignatureProvider
{
    DuplicateMethod Method { get; }

    Task<DuplicateSignature> ComputeAsync(
        DuplicateCandidate candidate,
        CancellationToken ct = default);
}
