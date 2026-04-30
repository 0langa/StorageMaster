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

    /// <summary>
    /// Resolves a known folder GUID to a filesystem path.
    /// Throws <see cref="IOException"/> on HRESULT failure instead of
    /// dereferencing a null pointer.
    /// </summary>
    internal static string GetKnownFolderPath(Guid folderId)
    {
        int hr = SHGetKnownFolderPath(ref folderId, 0, nint.Zero, out nint ptr);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        try
        {
            return Marshal.PtrToStringUni(ptr)
                ?? throw new IOException($"SHGetKnownFolderPath returned null for {folderId}");
        }
        finally
        {
            if (ptr != nint.Zero)
                Marshal.FreeCoTaskMem(ptr);
        }
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
