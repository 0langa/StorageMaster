using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

internal static class ScanFilePager
{
    internal const int PageSize = 50_000;

    /// <summary>
    /// Loads every persisted file deterministically. Older cleanup rules read
    /// only the largest 50,000 rows, silently omitting smaller matches.
    /// </summary>
    internal static async Task<IReadOnlyList<FileEntry>> LoadAllAsync(
        IScanRepository repository,
        long sessionId,
        CancellationToken cancellationToken)
    {
        var firstPage = await repository.GetLargestFilesAsync(
            sessionId,
            PageSize,
            cancellationToken);
        if (firstPage.Count < PageSize)
            return firstPage;

        var allFiles = new List<FileEntry>(firstPage);
        var offset = PageSize;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await repository.SearchFilesAsync(
                sessionId,
                filter: null,
                categoryFilter: null,
                sortColumn: "Size",
                descending: true,
                offset,
                PageSize,
                cancellationToken);
            allFiles.AddRange(page);
            if (page.Count < PageSize)
                return allFiles;
            offset = checked(offset + PageSize);
        }
    }
}
