using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// Windows implementation of <see cref="IFileSnapshotProvider"/>.
///
/// Opens the file handle and calls GetFileInformationByHandle to read
/// size, modification time, and NTFS file identity atomically enough for
/// race detection during hashing: if any attribute changes between the
/// before-hash and after-hash snapshots, the computed hash is discarded.
/// </summary>
public sealed class FileSnapshotProvider : IFileSnapshotProvider
{
    public async ValueTask<FileSnapshot?> TakeSnapshotAsync(
        string path,
        CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        try
        {
            if (!File.Exists(path)) return null;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            FileIdentity? identity = null;
            if (GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            {
                var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                identity = new FileIdentity(info.VolumeSerialNumber.ToString("X8"), fileIndex);
            }

            var fi = new FileInfo(path);
            return new FileSnapshot(
                path,
                identity,
                fi.Length,
                fi.LastWriteTimeUtc,
                fi.Attributes);
        }
        catch
        {
            // File disappeared or access denied between the exists check and open.
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint DwLowDateTime; public uint DwHighDateTime; }

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
}
