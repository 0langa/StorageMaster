using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>Persistence contract for scan sessions and their file/folder data.</summary>
public interface IScanRepository
{
    Task<ScanSession>  CreateSessionAsync(string rootPath, CancellationToken ct = default);
    Task<ScanSession?> GetSessionAsync(long sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<ScanSession>> GetRecentSessionsAsync(int count = 10, CancellationToken ct = default);
    Task UpdateSessionAsync(ScanSession session, CancellationToken ct = default);

    /// <summary>Bulk-insert file entries. Call in batches of ~500 for throughput.</summary>
    Task InsertFileEntriesAsync(IReadOnlyList<FileEntry> entries, CancellationToken ct = default);

    /// <summary>Upsert folder size records (created or updated as scan progresses).</summary>
    Task UpsertFolderEntriesAsync(IReadOnlyList<FolderEntry> entries, CancellationToken ct = default);

    Task<IReadOnlyList<FileEntry>> GetLargestFilesAsync(long sessionId, int topN, CancellationToken ct = default);
    Task<IReadOnlyList<FolderEntry>> GetLargestFoldersAsync(long sessionId, int topN, CancellationToken ct = default);

    /// <summary>Returns file-type category breakdown for a session.</summary>
    Task<IReadOnlyDictionary<FileTypeCategory, (long Count, long Bytes)>> GetCategoryBreakdownAsync(
        long sessionId, CancellationToken ct = default);

    Task DeleteSessionAsync(long sessionId, CancellationToken ct = default);
}
