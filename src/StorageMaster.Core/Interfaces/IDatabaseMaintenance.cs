namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Lets the UI report and reclaim the space StorageMaster's own database occupies.
/// <para>
/// Exposed as an interface so presentation code depends on the capability rather
/// than on the concrete SQLite context.
/// </para>
/// </summary>
public interface IDatabaseMaintenance
{
    /// <summary>
    /// Bytes the database occupies on disk, including its write-ahead log. The WAL
    /// is included deliberately: a long scan can leave one larger than many
    /// databases, and excluding it would understate the real footprint.
    /// </summary>
    Task<long> GetDatabaseSizeBytesAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the database file and returns the bytes reclaimed. Deleting scan
    /// history frees pages inside the file but never shrinks it, so without this
    /// the file only ever grows.
    /// </summary>
    Task<long> CompactAsync(CancellationToken ct = default);
}
