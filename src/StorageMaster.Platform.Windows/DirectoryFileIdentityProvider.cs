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

    // Field offsets inside FILE_ID_BOTH_DIR_INFO, resolved once instead of once
    // per directory entry. They are taken from the marshaller's own layout of the
    // declaration below rather than hand-derived, so the padding rules stay in
    // one place.
    private static readonly int NextEntryOffsetOffset = OffsetOf(nameof(FILE_ID_BOTH_DIR_INFO.NextEntryOffset));
    private static readonly int FileAttributesOffset = OffsetOf(nameof(FILE_ID_BOTH_DIR_INFO.FileAttributes));
    private static readonly int FileNameLengthOffset = OffsetOf(nameof(FILE_ID_BOTH_DIR_INFO.FileNameLength));
    private static readonly int FileIdOffset = OffsetOf(nameof(FILE_ID_BOTH_DIR_INFO.FileId));
    private static readonly int FileNameOffset = OffsetOf(nameof(FILE_ID_BOTH_DIR_INFO.FileName));

    private static int OffsetOf(string fieldName) =>
        Marshal.OffsetOf<FILE_ID_BOTH_DIR_INFO>(fieldName).ToInt32();

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
                    // Only four of the entry's fields are ever used, so they are read
                    // directly out of the buffer. Marshal.PtrToStructure marshalled
                    // the whole record per entry — a reflection-driven copy that also
                    // allocated a throwaway ShortName string for every file — and
                    // Marshal.OffsetOf recomputed the FileName offset every time.
                    var fileAttributes = (uint)Marshal.ReadInt32(buffer, offset + FileAttributesOffset);
                    var fileNameLength = (uint)Marshal.ReadInt32(buffer, offset + FileNameLengthOffset);
                    var nextEntryOffset = (uint)Marshal.ReadInt32(buffer, offset + NextEntryOffsetOffset);

                    // Directories are captured by their own pass; the scanner
                    // only asks for file identity here.
                    const uint fileAttributeDirectory = 0x10;
                    if ((fileAttributes & fileAttributeDirectory) == 0)
                    {
                        // FileName is a variable-length inline array; FileNameLength
                        // is in bytes and the struct declares only its first char.
                        var name = Marshal.PtrToStringUni(
                            buffer + offset + FileNameOffset,
                            (int)(fileNameLength / sizeof(char)));

                        if (!string.IsNullOrEmpty(name) && name != "." && name != "..")
                        {
                            var fileId = Marshal.ReadInt64(buffer, offset + FileIdOffset);
                            identities[name] = new FileIdentity(volumeSerial, (ulong)fileId);
                        }
                    }

                    if (nextEntryOffset == 0)
                        break;

                    offset += (int)nextEntryOffset;
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

    /// <summary>
    /// Layout reference for FILE_ID_BOTH_DIR_INFO. No instance is ever marshalled —
    /// the type exists so <see cref="Marshal.OffsetOf{T}(string)"/> can compute the
    /// field offsets once. Every field must stay declared (ShortName included):
    /// removing one would shift the offsets of everything after it.
    /// </summary>
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
