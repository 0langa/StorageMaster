using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateKeeperPolicy
{
    DuplicateCandidate ChooseKeeper(
        IReadOnlyList<DuplicateCandidate> candidates,
        KeeperPolicy policy);
}
