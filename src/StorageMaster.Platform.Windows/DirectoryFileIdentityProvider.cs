using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// Batches file-identity capture over a single directory handle using
/// <c>GetFileInformationByHandleEx(FileIdBothDirectoryInfo)</c>.
/// <para>
/// One directory open replaces one open per child file. The volume serial is a
/// property of the volume, so it is read once from the directory handle and
/// applies to every child.
/// </para>
/// <para>
/// The directory is opened with <c>FILE_SHARE_READ | WRITE | DELETE</c> and
/// without following reparse points, matching the no-follow guarantees the
/// scanner relies on elsewhere. Any failure returns <c>null</c> so the caller
/// falls back to per-file capture rather than losing identity.
/// </para>
/// </summary>
public sealed class DirectoryFileIdentityProvider : IDirectoryFileIdentityProvider
{
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileListDirectory = 0x0000_0001;
    private const uint FileShareAll = 0x0000_0007; // READ | WRITE | DELETE
    private const uint OpenExisting = 3;
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorMoreData = 234;

    public Task<IReadOnlyDictionary<string, FileIdentity>?> TryGetDirectoryIdentitiesAsync(
        string directoryPath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TryReadDirectory(directoryPath, ct));
    }

    private static IReadOnlyDictionary<string, FileIdentity>? TryReadDirectory(
        string directoryPath,
        CancellationToken ct)
    {
        using var handle = CreateFileW(
            directoryPath,
            FileListDirectory,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        if (!GetFileInformationByHandle(handle, out var directoryInfo))
            return null;

        var volumeSerial = directoryInfo.VolumeSerialNumber.ToString("X8");

        // 64 KB holds several hundred entries, so most directories need one call.
        const int bufferSize = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var identities = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
            var infoClass = FileIdBothDirectoryRestartInfo;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (!GetFileInformationByHandleEx(handle, infoClass, buffer, bufferSize))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles)
                        return identities;

                    // A single directory entry longer than the buffer, or any
                    // other failure, means the batch is incomplete. Returning
                    // null keeps the caller on the per-file path, which is slow
                    // but never loses identity.
                    return error == ErrorMoreData ? null : null;
                }

                // Subsequent calls continue rather than restart the enumeration.
                infoClass = FileIdBothDirectoryInfo;

                var offset = 0;
                while (true)
                {
                    var entry = (FILE_ID_BOTH_DIR_INFO)Marshal.PtrToStructure(
                        buffer + offset, typeof(FILE_ID_BOTH_DIR_INFO))!;

                    // FileName is a variable-length inline array; FileNameLength
                    // is in bytes and the struct declares only its first char.
                    var nameOffset = offset + Marshal.OffsetOf<FILE_ID_BOTH_DIR_INFO>(
                        nameof(FILE_ID_BOTH_DIR_INFO.FileName)).ToInt32();
                    var name = Marshal.PtrToStringUni(
                        buffer + nameOffset,
                        (int)(entry.FileNameLength / sizeof(char)));

                    if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                    {
                        // Directories are captured by their own pass; the scanner
                        // only asks for file identity here.
                        const uint fileAttributeDirectory = 0x10;
                        if ((entry.FileAttributes & fileAttributeDirectory) == 0)
                            identities[name] = new FileIdentity(volumeSerial, (ulong)entry.FileId);
                    }

                    if (entry.NextEntryOffset == 0)
                        break;

                    offset += (int)entry.NextEntryOffset;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        IntPtr lpFileInformation,
        int dwBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILE_ID_BOTH_DIR_INFO
    {
        public uint NextEntryOffset;
        public uint FileIndex;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public long EndOfFile;
        public long AllocationSize;
        public uint FileAttributes;
        public uint FileNameLength;
        public uint EaSize;
        public byte ShortNameLength;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 12)]
        public string ShortName;
        public long FileId;
        public char FileName;
    }
}
