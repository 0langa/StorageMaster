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
        var totals = folders.ToDictionary(f => f.FullPath, f => f.DirectSizeBytes);

        // Process children before their parents by sorting longest path first.
        var sorted = folders
            .OrderByDescending(f => f.FullPath.Length)
            .ToList();

        foreach (var folder in sorted)
        {
            var parent = Path.GetDirectoryName(folder.FullPath);
            if (parent is not null && totals.TryGetValue(parent, out long parentTotal))
                totals[parent] = parentTotal + totals[folder.FullPath];
        }

        return totals;
    }
}
