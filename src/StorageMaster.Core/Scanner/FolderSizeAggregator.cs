using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scanner;

/// <summary>
/// Computes recursive folder totals from a flat list of folder entries.
/// Algorithm: sort by path length descending (children before parents), then
/// propagate each folder's accumulated total into its parent's entry.
/// O(n log n) time, O(n) space.
/// </summary>
public static class FolderSizeAggregator
{
    public static Dictionary<string, long> Compute(IReadOnlyList<FolderEntry> folders)
    {
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.FullPath))
                continue;

            string normalizedPath;
            try
            {
                normalizedPath = ScanOptionValidator.NormalizeDirectoryPath(folder.FullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            totals[normalizedPath] = totals.GetValueOrDefault(normalizedPath) + folder.DirectSizeBytes;
        }

        // Process children before their parents by sorting longest path first.
        var sorted = totals.Keys
            .OrderByDescending(static path => path.Length)
            .ToList();

        foreach (var folderPath in sorted)
        {
            var parent = Path.GetDirectoryName(folderPath);
            if (parent is not null && totals.TryGetValue(parent, out long parentTotal))
                totals[parent] = parentTotal + totals[folderPath];
        }

        return totals;
    }
}
