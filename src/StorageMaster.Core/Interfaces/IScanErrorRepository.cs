using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IScanErrorRepository
{
    Task LogErrorsAsync(long sessionId, IReadOnlyList<ScanError> errors, CancellationToken ct = default);
    Task<IReadOnlyList<ScanError>> GetErrorsForSessionAsync(long sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<ScanError>> GetErrorsPageForSessionAsync(long sessionId, int offset, int limit, CancellationToken ct = default);
    Task<long> CountErrorsForSessionAsync(long sessionId, CancellationToken ct = default);
}
