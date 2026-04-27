using System.Runtime.InteropServices;

namespace StorageMaster.Platform.Windows.Interop;

internal static partial class Shell32Interop
{
    // shell32.dll exports only the W (Unicode) suffixed variants; the unsuffixed
    // names are preprocessor macros in the SDK headers and do not appear in the
    // DLL export table. [LibraryImport] does not auto-append W unlike [DllImport].
    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint SHEmptyRecycleBin(
        IntPtr hwnd,
        string? pszRootPath,
        EmptyRecycleBinFlags dwFlags);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHQueryRecycleBin(
        string? pszRootPath,
        ref SHQUERYRBINFO pSHQueryRBInfo);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(
        ref Guid rfid,
        uint dwFlags,
        nint hToken,
        out nint ppszPath);

    internal static string GetKnownFolderPath(Guid folderId)
    {
        SHGetKnownFolderPath(ref folderId, 0, nint.Zero, out nint ptr);
        string path = Marshal.PtrToStringUni(ptr)!;
        Marshal.FreeCoTaskMem(ptr);
        return path;
    }

    // ── SHFileOperation — used for silent recycle-bin deletion ───────────────
    // Using [DllImport] here because [LibraryImport] requires source generation and
    // SHFILEOPSTRUCT contains string fields that need special marshalling.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr  hwnd;
        public uint    wFunc;
        public string  pFrom;          // double-null-terminated list of source paths
        public string? pTo;
        public ushort  fFlags;
        public bool    fAnyOperationsAborted;
        public IntPtr  hNameMappings;
        public string? lpszProgressTitle;
    }

    internal const uint   FO_DELETE          = 0x0003;
    internal const ushort FOF_SILENT         = 0x0004;   // no progress dialog
    internal const ushort FOF_NOCONFIRMATION = 0x0010;   // no "are you sure?" dialog
    internal const ushort FOF_ALLOWUNDO      = 0x0040;   // send to Recycle Bin
    internal const ushort FOF_NOERRORUI      = 0x0400;   // suppress ALL error dialogs

    // ── Other structs / enums ─────────────────────────────────────────────────

    [Flags]
    internal enum EmptyRecycleBinFlags : uint
    {
        NoConfirmation = 0x00000001,
        NoProgressUI  = 0x00000002,
        NoSound       = 0x00000004,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHQUERYRBINFO
    {
        public int    cbSize;
        public long   i64Size;
        public long   i64NumItems;
    }

    internal const uint S_OK = 0;
}
