using StorageMaster.Core.Models;
using StorageMaster.UI.Converters;

namespace StorageMaster.UI.Pages;

/// <summary>
/// Lightweight view-model node used to populate the Folder Tree tab.
/// The tree is built from the flat list of loaded <see cref="FolderEntry"/> objects
/// by wiring each node to its parent based on the FullPath prefix.
/// </summary>
public sealed class FolderTreeNode
{
    public FolderEntry Folder { get; }

    /// <summary>Sorted child nodes (largest total size first).</summary>
    public List<FolderTreeNode> Children { get; } = [];

    /// <summary>Last path component — shown as the node label in the tree.</summary>
    public string DisplayName =>
        Path.GetFileName(Folder.FullPath) is { Length: > 0 } n ? n : Folder.FullPath;

    /// <summary>Human-readable recursive total size.</summary>
    public string SizeText => ByteSizeConverter.Format(Folder.TotalSizeBytes);

    /// <summary>Direct file count with thousands separator.</summary>
    public string FileCountText => $"{Folder.FileCount:N0} files";

    public long TotalSizeBytes => Folder.TotalSizeBytes;

    public FolderTreeNode(FolderEntry folder) => Folder = folder;

    /// <summary>Sorts <see cref="Children"/> by total size descending (largest first).</summary>
    internal void SortChildren() =>
        Children.Sort((a, b) => b.TotalSizeBytes.CompareTo(a.TotalSizeBytes));
}
