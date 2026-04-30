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

    // ── Structs / enums ───────────────────────────────────────────────────────

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
