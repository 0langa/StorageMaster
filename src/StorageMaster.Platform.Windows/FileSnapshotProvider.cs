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
    public ValueTask<FileSnapshot?> TakeSnapshotAsync(
        string path,
        CancellationToken ct = default)
    {
        // Deliberately synchronous. The work is one handle open plus one metadata
        // call; the former `await Task.Yield()` forced a thread-pool continuation
        // for every file hashed, guarded-deleted and analysed, buying nothing.
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadSnapshot(path));
    }

    private static FileSnapshot? ReadSnapshot(string path)
    {
        try
        {
            // No File.Exists pre-check: a missing file, a directory, or a denied
            // path all fail the open below and land in the same catch.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                return null;

            var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            var identity = new FileIdentity(info.VolumeSerialNumber.ToString("X8"), fileIndex);
            var size = checked((long)(((ulong)info.FileSizeHigh << 32) | info.FileSizeLow));
            var lastWriteFileTime = checked(
                (long)(((ulong)info.LastWriteTime.DwHighDateTime << 32) |
                       info.LastWriteTime.DwLowDateTime));
            return new FileSnapshot(
                path,
                identity,
                size,
                DateTime.FromFileTimeUtc(lastWriteFileTime),
                (FileAttributes)info.FileAttributes);
        }
        catch
        {
            // File is gone, is a directory, or access was denied.
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
