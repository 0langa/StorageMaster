namespace StorageMaster.Core.Models;

/// <summary>
/// Point-in-time snapshot of a file's stable identity attributes, captured
/// atomically enough for race detection during hashing.
/// </summary>
/// <param name="Path">Absolute path to the file.</param>
/// <param name="Identity">NTFS file identity (volume + file index), or null on non-NTFS volumes.</param>
/// <param name="SizeBytes">File length in bytes.</param>
/// <param name="LastWriteUtc">Last-write timestamp in UTC.</param>
/// <param name="Attributes">Raw file attributes.</param>
public sealed record FileSnapshot(
    string Path,
    FileIdentity? Identity,
    long SizeBytes,
    DateTime LastWriteUtc,
    FileAttributes Attributes)
{
    /// <summary>Returns true when all stable attributes still match <paramref name="other"/>.</summary>
    public bool IsIdenticalTo(FileSnapshot other) =>
        SizeBytes == other.SizeBytes &&
        LastWriteUtc == other.LastWriteUtc &&
        (Identity is null || other.Identity is null || Identity == other.Identity);
}
