using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageMaster.Platform.Windows.Interop;

/// <summary>
/// Holds a no-follow handle to one directory while destructive traversal is in progress.
/// The share mode permits readers only: rename/delete and reparse-point conversion require
/// delete or write access and therefore cannot race the guarded traversal.
/// </summary>
internal sealed class DirectoryTraversalGuard : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;

    internal DirectoryTraversalGuard(
        SafeFileHandle handle,
        string path,
        bool isReparsePoint,
        uint reparseTag)
    {
        _handle = handle;
        _path = path;
        IsReparsePoint = isReparsePoint;
        ReparseTag = reparseTag;
    }

    internal bool IsReparsePoint { get; }
    internal uint ReparseTag { get; }

    /// <summary>
    /// Marks the exact directory entry represented by this no-follow handle for deletion.
    /// The entry is removed when the handle closes, without reopening its path.
    /// </summary>
    internal void MarkForDeletion() =>
        DirectoryTraversalInterop.MarkForDeletion(_handle, _path);

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// Holds a no-follow read handle to a directory while a read-only traversal runs.
/// When delete access is available, the handle also blocks rename/delete replacement.
/// Protected ancestors can fall back to a weak read handle for analysis only; callers
/// must not use a weak guard to authorize later path-based mutation.
/// </summary>
internal sealed class DirectoryReadTraversalGuard : IDisposable
{
    private readonly SafeFileHandle _handle;

    internal DirectoryReadTraversalGuard(
        SafeFileHandle handle,
        bool isReparsePoint,
        uint reparseTag,
        bool blocksReplacement)
    {
        _handle = handle;
        IsReparsePoint = isReparsePoint;
        ReparseTag = reparseTag;
        BlocksReplacement = blocksReplacement;
    }

    internal bool IsReparsePoint { get; }
    internal uint ReparseTag { get; }
    internal bool BlocksReplacement { get; }

    public void Dispose() => _handle.Dispose();
}

internal static class DirectoryTraversalInterop
{
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorPrivilegeNotHeld = 1314;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;

    /// <summary>
    /// Opens the named directory itself rather than any reparse target. Returns null only
    /// when the entry vanished; all other inspection/open failures fail closed.
    /// </summary>
    internal static DirectoryTraversalGuard? TryOpenNoFollow(string path)
    {
        var handle = CreateFileW(
            path,
            FileReadAttributes | DeleteAccess,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return null;

            throw CreateIOException("Unable to lock directory for safe traversal", path, error);
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var info,
                (uint)Marshal.SizeOf<FileAttributeTagInfoBuffer>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw CreateIOException("Unable to inspect the locked directory", path, error);
        }

        var isReparsePoint = (info.FileAttributes & FileAttributeReparsePoint) != 0;
        var isDirectory = (info.FileAttributes & FileAttributeDirectory) != 0;
        if (!isReparsePoint && !isDirectory)
        {
            handle.Dispose();
            throw new IOException(
                $"Path changed from a directory before safe traversal could begin: {path}");
        }

        return new DirectoryTraversalGuard(handle, path, isReparsePoint, info.ReparseTag);
    }

    /// <summary>
    /// Opens the named directory itself for a long-running read-only traversal. A strong
    /// handle requests delete access and denies delete sharing, which blocks rename/delete
    /// replacement without blocking ordinary readers/writers. If ACLs or an existing
    /// handle make that impossible, a no-follow weak read handle supports analysis only.
    /// </summary>
    internal static DirectoryReadTraversalGuard? TryOpenNoFollowForReadTraversal(string path) =>
        TryOpenNoFollowForReadTraversal(path, attemptStrongGuard: true);

    /// <summary>Test seam can force the same weak fallback used after strong-open denial.</summary>
    internal static DirectoryReadTraversalGuard? TryOpenNoFollowForReadTraversal(
        string path,
        bool attemptStrongGuard)
    {
        SafeFileHandle? handle = null;
        var blocksReplacement = false;
        if (attemptStrongGuard)
        {
            handle = CreateFileW(
                path,
                FileReadAttributes | DeleteAccess,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                blocksReplacement = true;
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                handle = null;
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                    return null;

                if (error is not (ErrorAccessDenied or ErrorSharingViolation or ErrorPrivilegeNotHeld))
                    throw CreateIOException("Unable to lock directory for safe read traversal", path, error);
            }
        }

        if (handle is null)
        {
            handle = CreateFileW(
                path,
                FileReadAttributes,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error is ErrorFileNotFound or ErrorPathNotFound)
                    return null;

                throw CreateIOException("Unable to validate directory for safe read traversal", path, error);
            }
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out var info,
                (uint)Marshal.SizeOf<FileAttributeTagInfoBuffer>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw CreateIOException("Unable to inspect the locked directory", path, error);
        }

        var isReparsePoint = (info.FileAttributes & FileAttributeReparsePoint) != 0;
        var isDirectory = (info.FileAttributes & FileAttributeDirectory) != 0;
        if (!isReparsePoint && !isDirectory)
        {
            handle.Dispose();
            throw new IOException(
                $"Path changed from a directory before safe traversal could begin: {path}");
        }

        return new DirectoryReadTraversalGuard(
            handle,
            isReparsePoint,
            info.ReparseTag,
            blocksReplacement);
    }

    internal static void MarkForDeletion(SafeFileHandle handle, string path)
    {
        var disposition = new FileDispositionInfoBuffer { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfoBuffer>()))
        {
            throw CreateIOException("Unable to delete the guarded directory", path, Marshal.GetLastWin32Error());
        }
    }

    private static IOException CreateIOException(string operation, string path, int error) =>
        new($"{operation}: {path}", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfoBuffer
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoBuffer
    {
        // Win32 BOOLEAN is one byte, unlike the four-byte BOOL used by most APIs.
        internal byte DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInfoBuffer fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInfoBuffer fileInformation,
        uint bufferSize);
}
