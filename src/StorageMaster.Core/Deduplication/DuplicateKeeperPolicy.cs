using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Deduplication;

public sealed class DuplicateKeeperPolicy : IDuplicateKeeperPolicy
{
    public DuplicateCandidate ChooseKeeper(
        IReadOnlyList<DuplicateCandidate> candidates,
        KeeperPolicy policy)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("Keeper policy requires at least one candidate.");

        return policy switch
        {
            KeeperPolicy.Oldest => candidates
                .OrderBy(static candidate => candidate.File.ModifiedUtc)
                .ThenBy(static candidate => candidate.File.FullPath.Length)
                .First(),
            KeeperPolicy.ShortestPath => candidates
                .OrderBy(static candidate => candidate.File.FullPath.Length)
                .ThenByDescending(static candidate => candidate.File.ModifiedUtc)
                .First(),
            KeeperPolicy.LongestPath => candidates
                .OrderByDescending(static candidate => candidate.File.FullPath.Length)
                .ThenByDescending(static candidate => candidate.File.ModifiedUtc)
                .First(),
            _ => candidates
                .OrderByDescending(static candidate => candidate.File.ModifiedUtc)
                .ThenBy(static candidate => candidate.File.FullPath.Length)
                .First(),
        };
    }
}
