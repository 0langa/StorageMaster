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
    //
    // pFrom / pTo are IntPtr so we can pass double-null-terminated multi-path
    // lists (CharSet=Unicode string marshalling stops at the first embedded \0,
    // which breaks batch operations). Callers must allocate with
    // BuildPathListHGlobal and free with Marshal.FreeHGlobal.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint   wFunc;
        public IntPtr pFrom;    // double-null-terminated list — use BuildPathListHGlobal
        public IntPtr pTo;
        public ushort fFlags;
        public bool   fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    /// <summary>
    /// Allocates a native Unicode double-null-terminated path list from a
    /// managed string collection. Caller MUST free with Marshal.FreeHGlobal.
    /// Format: path1\0path2\0…pathN\0\0
    /// </summary>
    internal static IntPtr BuildPathListHGlobal(IEnumerable<string> paths)
    {
        // Build a single string with embedded nulls: "a\0b\0c\0\0"
        var sb = new System.Text.StringBuilder();
        foreach (var p in paths)
        {
            sb.Append(p);
            sb.Append('\0');
        }
        sb.Append('\0'); // final extra null → double-null termination

        // Allocate native Unicode buffer
        return Marshal.StringToHGlobalUni(sb.ToString());
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
        public int  cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    internal const uint S_OK = 0;
}
