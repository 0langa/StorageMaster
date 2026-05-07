using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

public interface IDriveHealthRepository
{
    Task SaveSnapshotsAsync(IReadOnlyList<DriveHealthSnapshot> snapshots, CancellationToken ct = default);
    Task<IReadOnlyList<DriveHealthSnapshot>> GetLatestSnapshotsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DriveHealthSnapshot>> GetHistoryAsync(string driveName, int limit = 100, CancellationToken ct = default);
}
