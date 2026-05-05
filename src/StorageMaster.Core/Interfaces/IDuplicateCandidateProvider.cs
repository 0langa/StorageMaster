using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateCandidateProvider
{
    Task<IReadOnlyList<DuplicateCandidate>> GetExactCandidatesAsync(
        DuplicateScanOptions options,
        CancellationToken ct = default);
}
