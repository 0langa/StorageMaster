namespace StorageMaster.Platform.Windows.Interop;

/// <summary>
/// Per-item IFileOperation progress sink that makes a Recycle Bin request
/// "recycle or nothing".
///
/// With FOF_NOCONFIRMATION the copy engine answers its own "this item cannot be
/// recycled, delete it permanently?" question with yes: an item on a drive with
/// no Recycle Bin (a network share, many removable drives), an item larger than
/// the bin's maximum size, or an item on a drive whose bin is turned off is
/// destroyed silently, and the operation still reports success. The engine tells
/// the sink which way it is going before it touches the item: PreDeleteItem
/// carries TSF_DELETE_RECYCLE_IF_POSSIBLE only when the item is headed for the
/// bin. Returning an error there stops the item, and every operation still
/// pending in the same IFileOperation, before anything is deleted.
///
/// One sink is attached to exactly one requested path, so its record never has
/// to be matched back to a path. Callbacks may arrive on an engine thread, so
/// the state is written and read with volatile semantics.
/// </summary>
internal sealed class RecycleOnlyProgressSink : IFileOperationProgressSink
{
    private int _refusedPermanentDelete;
    private int _recycled;
    private int _destroyedWithoutRecycle;

    internal RecycleOnlyProgressSink(string path) => Path = path;

    /// <summary>The requested path this sink was attached to.</summary>
    internal string Path { get; }

    /// <summary>
    /// The engine was about to delete this item (or something inside it) without
    /// the Recycle Bin, and the sink stopped it. Nothing of this item was deleted.
    /// </summary>
    internal bool RefusedPermanentDelete => Volatile.Read(ref _refusedPermanentDelete) != 0;

    /// <summary>The engine confirmed the item now lives in the Recycle Bin.</summary>
    internal bool Recycled => Volatile.Read(ref _recycled) != 0;

    /// <summary>
    /// The engine reported a successful delete that did not produce a Recycle Bin
    /// item. The pre-delete check exists to make this impossible; it is recorded
    /// so that a shell which skips PreDeleteItem is at least visible in the log.
    /// </summary>
    internal bool DestroyedWithoutRecycle => Volatile.Read(ref _destroyedWithoutRecycle) != 0;

    public int PreDeleteItem(uint dwFlags, IntPtr psiItem)
    {
        if ((dwFlags & FileOperationInterop.TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0)
            return FileOperationInterop.S_OK;

        Volatile.Write(ref _refusedPermanentDelete, 1);
        return FileOperationInterop.E_ABORT;
    }

    public int PostDeleteItem(uint dwFlags, IntPtr psiItem, int hrDelete, IntPtr psiNewlyCreated)
    {
        if (hrDelete >= 0)
        {
            if (psiNewlyCreated != IntPtr.Zero)
                Volatile.Write(ref _recycled, 1);
            else
                Volatile.Write(ref _destroyedWithoutRecycle, 1);
        }

        return FileOperationInterop.S_OK;
    }

    // Everything else is informational for a delete; S_OK lets the engine continue.
    public int StartOperations() => FileOperationInterop.S_OK;
    public int FinishOperations(int hrResult) => FileOperationInterop.S_OK;
    public int PreRenameItem(uint dwFlags, IntPtr psiItem, IntPtr pszNewName) => FileOperationInterop.S_OK;
    public int PostRenameItem(uint dwFlags, IntPtr psiItem, IntPtr pszNewName, int hrRename, IntPtr psiNewlyCreated) => FileOperationInterop.S_OK;
    public int PreMoveItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName) => FileOperationInterop.S_OK;
    public int PostMoveItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrMove, IntPtr psiNewlyCreated) => FileOperationInterop.S_OK;
    public int PreCopyItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName) => FileOperationInterop.S_OK;
    public int PostCopyItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrCopy, IntPtr psiNewlyCreated) => FileOperationInterop.S_OK;
    public int PreNewItem(uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName) => FileOperationInterop.S_OK;
    public int PostNewItem(uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName, IntPtr pszTemplateName, uint dwFileAttributes, int hrNew, IntPtr psiNewItem) => FileOperationInterop.S_OK;
    public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => FileOperationInterop.S_OK;
    public int ResetTimer() => FileOperationInterop.S_OK;
    public int PauseTimer() => FileOperationInterop.S_OK;
    public int ResumeTimer() => FileOperationInterop.S_OK;
}
