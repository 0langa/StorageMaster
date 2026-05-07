using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDriveHealthProvider
{
    /// <summary>
    /// Reads current drive health information. Providers must return
    /// Unknown/Unsupported when telemetry is unavailable rather than guessing.
    /// </summary>
    Task<IReadOnlyList<DriveHealthSnapshot>> GetHealthAsync(CancellationToken ct = default);
}
