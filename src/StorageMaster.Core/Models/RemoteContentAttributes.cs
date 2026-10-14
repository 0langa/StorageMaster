namespace StorageMaster.Core.Models;

/// <summary>
/// Attribute bits Windows sets on a file whose content is not stored on the local
/// disk: OneDrive Files On-Demand and other Cloud Files placeholders that are
/// online-only, and files moved to offline (HSM) storage.
/// <para>
/// Reading such a file's data makes Windows fetch it — a download of the whole file
/// for OneDrive — and deleting it frees no local space while removing the copy that
/// does exist. Size figures for them are logical sizes, not local allocation.
/// </para>
/// </summary>
public static class RemoteContentAttributes
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: data is fetched when it is read.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Either bit means the file's data is not on this disk.</summary>
    public const FileAttributes Mask = RecallOnDataAccess | FileAttributes.Offline;

    public static bool IsRemote(FileAttributes attributes) => (attributes & Mask) != 0;
}
