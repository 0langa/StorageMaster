using System.Runtime.InteropServices;

namespace StorageMaster.Platform.Windows.Interop;

internal static partial class Shell32Interop
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint SHEmptyRecycleBin(
        IntPtr hwnd,
        string? pszRootPath,
        EmptyRecycleBinFlags dwFlags);

    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryRecycleBin(
        [MarshalAs(UnmanagedType.LPWStr)] string? pszRootPath,
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
