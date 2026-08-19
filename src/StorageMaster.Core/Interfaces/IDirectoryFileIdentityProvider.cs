using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Reads stable identity for every file in a directory in one pass.
/// <para>
/// <see cref="IFileIdentityProvider"/> opens a handle per file. During a scan
/// that is the dominant cost: throughput ends up bound by file count rather than
/// bytes, which is why trees of many small files scan far slower per megabyte
/// than trees of large ones. Windows can return the identity of every child from
/// a single directory handle, turning N opens into one.
/// </para>
/// <para>
/// This is an optimisation only. It never weakens the safety contract: a file
/// whose identity is missing from the batch must still fall back to the per-file
/// provider, and a file with no identity at all stays ineligible for
/// scan-backed destructive actions exactly as before.
/// </para>
/// </summary>
public interface IDirectoryFileIdentityProvider
{
    /// <summary>
    /// Returns identities keyed by file name (not full path), or <c>null</c> when
    /// the directory cannot be read in bulk and the caller should fall back to
    /// per-file capture. Implementations must not throw for ordinary access
    /// failures.
    /// </summary>
    Task<IReadOnlyDictionary<string, FileIdentity>?> TryGetDirectoryIdentitiesAsync(
        string directoryPath,
        CancellationToken ct = default);
}
