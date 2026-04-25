using System.Runtime.InteropServices;

namespace StorageMaster.Platform.Windows.Interop;

internal static partial class Shell32Interop
{
    /// <summary>
    /// Empties the Recycle Bin for the specified drive (null = all drives).
    /// </summary>
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint SHEmptyRecycleBin(
        IntPtr hwnd,
        string? pszRootPath,
        EmptyRecycleBinFlags dwFlags);

    /// <summary>
    /// Retrieves the size and item count of the Recycle Bin.
    /// </summary>
    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryRecycleBin(
        [MarshalAs(UnmanagedType.LPWStr)] string? pszRootPath,
        ref SHQUERYRBINFO pSHQueryRBInfo);

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
