using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// Windows implementation of IFileDeleter.
///
/// Performance strategy:
///   • When a batch of requests are ALL RecycleBin-mode real deletions, the
///     entire list is sent to the Recycle Bin in ONE SHFileOperation call.
///     This is orders-of-magnitude faster than calling SHFileOperation once
///     per file (the shell updates the Recycle Bin index once, not N times).
///   • On batch failure (partial error) the batch falls back to per-file mode
///     so individual error messages are captured.
///   • Permanent deletion uses parallel File.Delete / Directory.Delete.
///
/// Error handling:
///   • SHFileOperation is called with FOF_NOERRORUI | FOF_SILENT so the shell
///     NEVER shows its own error dialogs. All errors are surfaced as
///     DeletionOutcome.Success=false and shown in the app's report dialog.
///
/// The sentinel path "::RecycleBin::" calls SHEmptyRecycleBin instead.
/// </summary>
public sealed class FileDeleter : IFileDeleter
{
    private readonly ILogger<FileDeleter> _logger;

    private const int MaxConcurrency = 8; // raised from 4; permanent deletes are lightweight

    public FileDeleter(ILogger<FileDeleter> logger) => _logger = logger;

    // ── Public interface ────────────────────────────────────────────────────

    public async Task<DeletionOutcome> DeleteAsync(
        DeletionRequest   request,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (request.DryRun)
        {
            long est = EstimateSize(request.Path);
            _logger.LogInformation("[DryRun] Would delete {Path} (~{Size} B)", request.Path, est);
            return new DeletionOutcome(request.Path, true, est);
        }

        if (request.Path == "::RecycleBin::")
            return EmptyRecycleBin();

        try
        {
            long size = EstimateSize(request.Path);
            if (request.Method == DeletionMethod.RecycleBin)
                DeleteToRecycleBin([request.Path]);
            else
                DeletePermanently(request.Path);
            _logger.LogInformation("Deleted {Path} ({Size} B)", request.Path, size);
            return new DeletionOutcome(request.Path, true, size);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete {Path}", request.Path);
            return new DeletionOutcome(request.Path, false, 0, ex.Message);
        }
    }

    public async IAsyncEnumerable<DeletionOutcome> DeleteManyAsync(
        IReadOnlyList<DeletionRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) yield break;

        // ── Fast path: batch all RecycleBin real deletions in one shell call ──
        // This is the common cleanup case and is dramatically faster than N calls.
        bool allRecycleBin = requests.All(r => !r.DryRun && r.Method == DeletionMethod.RecycleBin
                                                          && r.Path != "::RecycleBin::");
        if (allRecycleBin && requests.Count > 1)
        {
            await foreach (var o in BatchRecycleBinAsync(requests, cancellationToken))
                yield return o;
            yield break;
        }

        // ── Normal path: parallel per-file ───────────────────────────────────
        await foreach (var o in ParallelDeleteAsync(requests, cancellationToken))
            yield return o;
    }

    // ── Batch Recycle Bin (fast path) ───────────────────────────────────────

    private async IAsyncEnumerable<DeletionOutcome> BatchRecycleBinAsync(
        IReadOnlyList<DeletionRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        // Measure sizes before deletion (best-effort, parallel for speed)
        var sizes = await Task.Run(() =>
            requests.AsParallel().AsOrdered()
                    .Select(r => EstimateSize(r.Path))
                    .ToList(), cancellationToken);

        var paths = requests.Select(r => r.Path).ToList();
        var pFrom = Shell32Interop.BuildPathListHGlobal(paths);
        try
        {
            var op = new Shell32Interop.SHFILEOPSTRUCT
            {
                hwnd   = IntPtr.Zero,
                wFunc  = Shell32Interop.FO_DELETE,
                pFrom  = pFrom,
                fFlags = (ushort)(Shell32Interop.FOF_ALLOWUNDO      |
                                  Shell32Interop.FOF_NOCONFIRMATION  |
                                  Shell32Interop.FOF_NOERRORUI       |
                                  Shell32Interop.FOF_SILENT),
            };
            int rc = Shell32Interop.SHFileOperation(ref op);

            if (rc == 0)
            {
                // All succeeded
                _logger.LogInformation("Batch recycle: {Count} items", requests.Count);
                for (int i = 0; i < requests.Count; i++)
                    yield return new DeletionOutcome(requests[i].Path, true, sizes[i]);
                yield break;
            }
            _logger.LogWarning("Batch recycle failed (rc=0x{Rc:X}), falling back to per-file", rc);
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
        }

        // Fallback: per-file so we get individual error messages
        await foreach (var o in ParallelDeleteAsync(requests, cancellationToken))
            yield return o;
    }

    // ── Parallel per-file (normal path) ────────────────────────────────────

    private async IAsyncEnumerable<DeletionOutcome> ParallelDeleteAsync(
        IReadOnlyList<DeletionRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var semaphore = new SemaphoreSlim(MaxConcurrency);
        var channel   = System.Threading.Channels.Channel.CreateUnbounded<DeletionOutcome>();

        var producer = Task.Run(async () =>
        {
            var tasks = requests.Select(async req =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var outcome = await DeleteAsync(req, cancellationToken);
                    await channel.Writer.WriteAsync(outcome, cancellationToken);
                }
                finally { semaphore.Release(); }
            });
            try   { await Task.WhenAll(tasks); }
            finally { channel.Writer.Complete(); }
        }, cancellationToken);

        await foreach (var outcome in channel.Reader.ReadAllAsync(cancellationToken))
            yield return outcome;

        await producer;
    }

    // ── Deletion helpers ────────────────────────────────────────────────────

    private static void DeleteToRecycleBin(IReadOnlyList<string> paths)
    {
        var pFrom = Shell32Interop.BuildPathListHGlobal(paths);
        try
        {
            var op = new Shell32Interop.SHFILEOPSTRUCT
            {
                hwnd   = IntPtr.Zero,
                wFunc  = Shell32Interop.FO_DELETE,
                pFrom  = pFrom,
                fFlags = (ushort)(Shell32Interop.FOF_ALLOWUNDO      |
                                  Shell32Interop.FOF_NOCONFIRMATION  |
                                  Shell32Interop.FOF_NOERRORUI       |
                                  Shell32Interop.FOF_SILENT),
            };
            int rc = Shell32Interop.SHFileOperation(ref op);
            if (rc != 0)
                throw new IOException(
                    $"SHFileOperation returned 0x{rc:X8} for: {string.Join(", ", paths)}");
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
        }
    }

    private static void DeletePermanently(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
    }

    private DeletionOutcome EmptyRecycleBin()
    {
        try
        {
            var query = new Shell32Interop.SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf<Shell32Interop.SHQUERYRBINFO>()
            };
            Shell32Interop.SHQueryRecycleBin(null, ref query);
            long freed = query.i64Size;

            Shell32Interop.SHEmptyRecycleBin(
                IntPtr.Zero, null,
                Shell32Interop.EmptyRecycleBinFlags.NoConfirmation |
                Shell32Interop.EmptyRecycleBinFlags.NoProgressUI   |
                Shell32Interop.EmptyRecycleBinFlags.NoSound);

            _logger.LogInformation("Recycle Bin emptied. Freed {Size} B", freed);
            return new DeletionOutcome("::RecycleBin::", true, freed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to empty Recycle Bin");
            return new DeletionOutcome("::RecycleBin::", false, 0, ex.Message);
        }
    }

    private static long EstimateSize(string path)
    {
        try
        {
            if (File.Exists(path))      return new FileInfo(path).Length;
            if (Directory.Exists(path)) return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { /* best-effort */ }
        return 0;
    }
}
