using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Platform.Windows;

public sealed class FileIdentityProvider : IFileIdentityProvider
{
    public Task<FileIdentity?> GetIdentityAsync(string path, CancellationToken ct = default)
    {
        // Deliberately synchronous. The work is a single metadata call, and the
        // former `await Task.Yield()` forced a thread-pool continuation for every
        // file in the scan — millions of scheduling hops that bought nothing.
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ReadIdentity(path));
    }

    private static FileIdentity? ReadIdentity(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            return null;

        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return new FileIdentity(info.VolumeSerialNumber.ToString("X8"), fileIndex);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

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
}
