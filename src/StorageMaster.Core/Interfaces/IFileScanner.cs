using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Scans a folder tree and produces FileEntry / FolderEntry results.
/// Implementations MUST be thread-safe and MUST support cancellation.
/// </summary>
public interface IFileScanner
{
    /// <summary>
    /// Begins a new scan session. Returns the persisted session object.
    /// Progress is reported via <paramref name="progress"/> roughly every 200ms.
    /// </summary>
    Task<ScanSession> ScanAsync(
        ScanOptions             options,
        IProgress<ScanProgress> progress,
        CancellationToken       cancellationToken = default);

    /// <summary>
    /// Streams top-N largest files from the most recent completed session.
    /// Can be called while a scan is in progress for incremental display.
    /// </summary>
    IAsyncEnumerable<FileEntry> GetLargestFilesAsync(
        long          sessionId,
        int           topN              = 100,
        CancellationToken cancellationToken = default);

    /// <summary>Streams all folder entries for a session, ordered by TotalSizeBytes desc.</summary>
    IAsyncEnumerable<FolderEntry> GetLargestFoldersAsync(
        long          sessionId,
        int           topN              = 100,
        CancellationToken cancellationToken = default);
}
