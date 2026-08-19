using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

internal static class ScanFilePager
{
    internal const int PageSize = 50_000;

    /// <summary>
    /// Upper bound for a grown page.
    ///
    /// The repository pages with LIMIT/OFFSET, and SQLite has to produce and discard
    /// every row before the offset, so a fixed page size makes a full load cost
    /// O(n²/PageSize) — on a million-file session the twenty pages together scan an
    /// order of magnitude more rows than a single ordered pass. Doubling the page each
    /// time keeps the discarded work proportional to the rows actually returned (the
    /// last page dominates the sum), and the cap bounds how many rows one query has to
    /// materialise at once. The real fix is a keyset cursor on
    /// (SizeBytes, FullPath) in IScanRepository; this keeps the cost linear until then.
    /// </summary>
    internal const int MaxPageSize = 200_000;

    internal static Task<IReadOnlyList<FileEntry>> LoadAllAsync(
        IScanRepository repository,
        long sessionId,
        CancellationToken cancellationToken) =>
        LoadAllAsync(repository, sessionId, predicate: null, cancellationToken);

    /// <summary>
    /// Loads every persisted file deterministically. Older cleanup rules read
    /// only the largest 50,000 rows, silently omitting smaller matches.
    ///
    /// <paramref name="predicate"/>, when supplied, is applied page by page so a rule
    /// scoped to one subtree retains only its own matches instead of one
    /// <see cref="FileEntry"/> per file in the whole scan session.
    /// </summary>
    internal static async Task<IReadOnlyList<FileEntry>> LoadAllAsync(
        IScanRepository repository,
        long sessionId,
        Func<FileEntry, bool>? predicate,
        CancellationToken cancellationToken)
    {
        var firstPage = await repository.GetLargestFilesAsync(
            sessionId,
            PageSize,
            cancellationToken);

        var allFiles = new List<FileEntry>();
        AddMatching(allFiles, firstPage, predicate);
        if (firstPage.Count < PageSize)
            return allFiles;

        var offset = PageSize;
        var pageSize = PageSize;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageSize = Math.Min(pageSize * 2, MaxPageSize);
            var page = await repository.SearchFilesAsync(
                sessionId,
                filter: null,
                categoryFilter: null,
                sortColumn: "Size",
                descending: true,
                offset,
                pageSize,
                cancellationToken);
            AddMatching(allFiles, page, predicate);
            if (page.Count < pageSize)
                return allFiles;
            offset = checked(offset + pageSize);
        }
    }

    private static void AddMatching(
        List<FileEntry> destination,
        IReadOnlyList<FileEntry> page,
        Func<FileEntry, bool>? predicate)
    {
        if (predicate is null)
        {
            destination.AddRange(page);
            return;
        }

        for (var index = 0; index < page.Count; index++)
        {
            if (predicate(page[index]))
                destination.Add(page[index]);
        }
    }
}
