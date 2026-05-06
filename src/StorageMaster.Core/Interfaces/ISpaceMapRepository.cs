using StorageMaster.Core.Models;
using StorageMaster.Core.SpaceMap;

namespace StorageMaster.Core.Interfaces;

public interface ISpaceMapRepository
{
    Task<IReadOnlyList<ScanSession>> GetSessionRootCandidatesAsync(CancellationToken ct = default);

    Task<ScanSession?> GetPreviousComparableSessionAsync(
        long currentSessionId,
        CancellationToken ct = default);

    Task<IReadOnlyList<SpaceMapNode>> GetFolderChildrenWithSizesAsync(
        long sessionId,
        string folderPath,
        SpaceMapNodeKind? kindFilter,
        long minimumSizeBytes,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<SpaceMapNode>> GetLargestFilesUnderFolderAsync(
        long sessionId,
        string folderPath,
        int limit,
        CancellationToken ct = default);

    Task<ScanDeltaSummary> GetScanDeltaAsync(
        long currentSessionId,
        long previousSessionId,
        int limit,
        CancellationToken ct = default);
}
