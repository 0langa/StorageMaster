using System.Runtime.InteropServices;

namespace StorageMaster.Platform.Windows.Interop;

/// <summary>
/// COM interop for IFileOperation — the modern replacement for SHFileOperation.
/// Vista+ only (all Windows versions we support). Used by Windows Explorer itself.
///
/// Benefits over SHFileOperation:
///   • Not flagged by AV heuristics that associate SHFileOperation+FOF_SILENT with malware
///   • Transactional: all items queued, then one PerformOperations() call
///   • Proper HRESULT error reporting per item via progress sink (optional)
///   • COM-based creation: CLSID doesn't appear in PE import table
/// </summary>
internal static class FileOperationInterop
{
    // FileOperation coclass CLSID — Windows Vista+
    private static readonly Guid CLSID_FileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    // IShellItem IID — used with SHCreateItemFromParsingName
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    // FOF_* flags — same constants as SHFileOperation; valid for IFileOperation.SetOperationFlags
    internal const uint FOF_ALLOWUNDO      = 0x0040; // send to Recycle Bin (not permanent)
    internal const uint FOF_NOCONFIRMATION = 0x0010; // skip shell confirmation dialog
    internal const uint FOF_NOERRORUI      = 0x0400; // suppress shell error dialogs

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    /// <summary>
    /// Creates a new IFileOperation COM object. Always call <see cref="Marshal.ReleaseComObject"/>
    /// when done.
    /// </summary>
    internal static IFileOperation CreateFileOperation()
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation)!)
            ?? throw new InvalidOperationException("Failed to create IFileOperation COM object.");
        return (IFileOperation)instance;
    }

    /// <summary>
    /// Creates an IShellItem from an absolute file-system path. Caller should
    /// <see cref="Marshal.ReleaseComObject"/> the returned object when done.
    /// </summary>
    internal static IShellItem CreateShellItem(string path)
    {
        var iid = IID_IShellItem;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return item;
    }
}

// ── IShellItem (vtable order from shobjidl_core.h) ──────────────────────────

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint hint, out int piOrder);
}

// ── IFileOperation (vtable order from shobjidl_core.h) ───────────────────────
//
// Every method must be declared in vtable order to avoid calling the wrong slot.
// Methods we don't use are declared with correct signatures but simply not called.

[ComImport]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    // slot 3
    [PreserveSig] int Advise(IntPtr pfops, out uint pdwCookie);
    // slot 4
    void Unadvise(uint dwCookie);
    // slot 5  ← we call this
    void SetOperationFlags(uint dwOperationFlags);
    // slot 6
    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
    // slot 7
    void SetProgressDialog(IntPtr popd);
    // slot 8
    void SetProperties(IntPtr pproparray);
    // slot 9
    void SetOwnerWindow(IntPtr hwndOwner);
    // slot 10
    void ApplyPropertiesToItem([MarshalAs(UnmanagedType.Interface)] object psiItem);
    // slot 11
    void ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object punkItems);
    // slot 12
    void RenameItem([MarshalAs(UnmanagedType.Interface)] object psiItem,
                   [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
                   IntPtr pfopsItem);
    // slot 13
    void RenameItems([MarshalAs(UnmanagedType.Interface)] object pUnkItems,
                    [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    // slot 14
    void MoveItem([MarshalAs(UnmanagedType.Interface)] object psiItem,
                 [MarshalAs(UnmanagedType.Interface)] object psiDestinationFolder,
                 [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
                 IntPtr pfopsItem);
    // slot 15
    void MoveItems([MarshalAs(UnmanagedType.Interface)] object punkItems,
                  [MarshalAs(UnmanagedType.Interface)] object psiDestinationFolder);
    // slot 16
    void CopyItem([MarshalAs(UnmanagedType.Interface)] object psiItem,
                 [MarshalAs(UnmanagedType.Interface)] object psiDestinationFolder,
                 [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName,
                 IntPtr pfopsItem);
    // slot 17
    void CopyItems([MarshalAs(UnmanagedType.Interface)] object punkItems,
                  [MarshalAs(UnmanagedType.Interface)] object psiDestinationFolder);
    // slot 18  ← we call this
    void DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, IntPtr pfopsItem);
    // slot 19
    void DeleteItems([MarshalAs(UnmanagedType.Interface)] object punkItems);
    // slot 20
    [return: MarshalAs(UnmanagedType.U4)]
    uint NewItem([MarshalAs(UnmanagedType.Interface)] object psiDestinationFolder,
                uint dwFileAttributes,
                [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName,
                IntPtr pfopsItem);
    // slot 21  ← we call this; [PreserveSig] to get HRESULT directly
    [PreserveSig] int PerformOperations();
    // slot 22  ← we call this to detect partial failures
    [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
}
