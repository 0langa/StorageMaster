using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scanner;

/// <summary>
/// Remembers which files a scan has already counted, so a file with several hard
/// links adds its size to folder and session totals once.
/// <para>
/// Hard links are separate names for one file: the data exists once on disk.
/// Windows uses them heavily (WinSxS links most of System32), and counting every
/// name made a system drive look far fuller than it is. The first name the scan
/// reaches carries the size; every further name is still listed, with its size,
/// but adds nothing to the totals. Files without a stable identity, or with a
/// placeholder file id, are always counted, as before.
/// </para>
/// <para>
/// Keyed by volume and file index only — 8 bytes per file plus set overhead — so
/// the cost stays small on scans of millions of files. Thread-safe: the parallel
/// scanner calls it from several workers.
/// </para>
/// </summary>
public sealed class HardLinkTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<ulong>> _seenByVolume = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True the first time a file is seen, false for every further hard link to it.
    /// A null identity, or a file index that is not a real id, is always the first.
    /// </summary>
    public bool IsFirstLink(FileIdentity? identity)
    {
        if (identity is null || !IsUniqueFileIndex(identity.FileIndex))
            return true;

        lock (_gate)
        {
            if (!_seenByVolume.TryGetValue(identity.VolumeSerial, out var seen))
            {
                seen = [];
                _seenByVolume[identity.VolumeSerial] = seen;
            }

            return seen.Add(identity.FileIndex);
        }
    }

    /// <summary>
    /// A file system that cannot give a file a unique 64-bit id reports 0 or all ones
    /// instead: ReFS for files whose 128-bit id does not fit, and some network and
    /// user-mode (FUSE-style) file systems for every file. Unrelated files share
    /// those values, so they prove nothing about hard links, and treating them as one
    /// file would leave all but the first out of the totals.
    /// </summary>
    private static bool IsUniqueFileIndex(ulong fileIndex) =>
        fileIndex is not (0UL or ulong.MaxValue);

    /// <summary>The bytes <paramref name="entry"/> adds to folder and session totals.</summary>
    public static long CountedBytes(FileEntry entry) =>
        entry.IsAdditionalHardLink ? 0 : entry.SizeBytes;
}
