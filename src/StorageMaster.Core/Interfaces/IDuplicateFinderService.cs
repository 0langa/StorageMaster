using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDuplicateFinderService
{
    Task<DuplicateRun> RunAsync(
        DuplicateScanOptions options,
        IProgress<DuplicateDetectionProgress>? progress = null,
        CancellationToken ct = default);
}
