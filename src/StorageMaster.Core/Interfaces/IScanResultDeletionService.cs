using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IScanResultDeletionService
{
    Task<DeletionOutcome> DeleteAsync(
        FileEntry file,
        DeletionMethod method,
        CancellationToken ct = default);
}
