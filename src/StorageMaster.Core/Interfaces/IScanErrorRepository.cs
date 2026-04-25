using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IScanErrorRepository
{
    Task LogErrorsAsync(long sessionId, IReadOnlyList<ScanError> errors, CancellationToken ct = default);
    Task<IReadOnlyList<ScanError>> GetErrorsForSessionAsync(long sessionId, CancellationToken ct = default);
}
