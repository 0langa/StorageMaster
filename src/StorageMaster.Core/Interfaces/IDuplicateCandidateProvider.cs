using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Returns candidate files from a completed scan session.
/// Implemented by <c>DuplicateRepository</c> which issues the optimised SQL.
/// </summary>
public interface IDuplicateCandidateProvider
{
    /// <summary>
    /// Fetch candidates using a method-tailored query.
    /// For exact methods (<see cref="DuplicateCandidateQuery.RequireSameSizeBucket"/> = true)
    /// the SQL adds a sub-query that restricts to sizes appearing more than once.
    /// For fuzzy/normalized methods (<c>RequireSameSizeBucket = false</c>) all
    /// files matching extension/category/path filters are returned.
    /// </summary>
    Task<IReadOnlyList<DuplicateCandidate>> GetCandidatesAsync(
        DuplicateCandidateQuery query,
        CancellationToken ct = default);
}
