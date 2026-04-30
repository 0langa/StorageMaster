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
///     entire list is sent to the Recycle Bin in ONE IFileOperation call.
///     This is orders-of-magnitude faster than calling the API once per file
///     (the shell updates the Recycle Bin index once, not N times).
///   • On batch failure (partial error) the batch falls back to per-file mode
///     so individual error messages are captured.
///   • Permanent deletion uses parallel File.Delete / Directory.Delete.
///
/// Error handling:
///   • IFileOperation is called with FOF_NOERRORUI | FOF_NOCONFIRMATION so the
///     shell NEVER shows its own error dialogs. All errors are surfaced as
///     DeletionOutcome.Success=false and shown in the app's report dialog.
///   • IFileOperation is the modern Vista+ replacement for SHFileOperation
///     (used by Explorer.exe itself). It is not flagged by AV heuristics.
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
                RecyclePathsViaIFileOperation([request.Path]);
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
        bool batchSucceeded = false;
        try
        {
            RecyclePathsViaIFileOperation(paths);
            batchSucceeded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch recycle failed, falling back to per-file");
        }

        if (batchSucceeded)
        {
            _logger.LogInformation("Batch recycle: {Count} items", requests.Count);
            for (int i = 0; i < requests.Count; i++)
                yield return new DeletionOutcome(requests[i].Path, true, sizes[i]);
            yield break;
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

    /// <summary>
    /// Sends one or more paths to the Recycle Bin using IFileOperation — the
    /// modern COM API used by Explorer.exe itself. Unlike the legacy
    /// SHFileOperation + FOF_SILENT combination, this approach is not flagged
    /// by antivirus heuristics that associate SHFileOperation's stealth flags
    /// with malware file deletion.
    /// </summary>
    private static void RecyclePathsViaIFileOperation(IReadOnlyList<string> paths)
    {
        var fo = FileOperationInterop.CreateFileOperation();
        try
        {
            fo.SetOperationFlags(
                FileOperationInterop.FOF_ALLOWUNDO      |   // send to Recycle Bin
                FileOperationInterop.FOF_NOCONFIRMATION |   // no "are you sure?" dialog
                FileOperationInterop.FOF_NOERRORUI);        // suppress shell error dialogs

            fo.SetOwnerWindow(IntPtr.Zero);

            foreach (var path in paths)
            {
                var item = FileOperationInterop.CreateShellItem(path);
                try
                {
                    fo.DeleteItem(item, IntPtr.Zero);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }

            int hr = fo.PerformOperations();
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);

            if (fo.GetAnyOperationsAborted())
                throw new IOException(
                    $"IFileOperation: one or more items could not be recycled ({paths.Count} items).");
        }
        finally
        {
            Marshal.ReleaseComObject(fo);
        }
    }

    /// <summary>
    /// Permanently deletes a file or directory. Junction/symlink-safe:
    /// reparse points are removed as links only — their targets are NOT
    /// recursively deleted. This prevents destroying data outside the
    /// intended directory tree.
    /// </summary>
    internal static void DeletePermanently(string path)
    {
        if (Directory.Exists(path))
        {
            // If the directory itself is a reparse point (junction/symlink),
            // delete the link only — never recurse into the target.
            if (IsReparsePoint(path))
            {
                Directory.Delete(path, recursive: false);
                return;
            }
            DeleteDirectoryRecursiveSafe(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Recursive directory delete that skips into reparse-point subdirectories,
    /// removing them as links only.
    /// </summary>
    private static void DeleteDirectoryRecursiveSafe(string dir)
    {
        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            if (IsReparsePoint(subDir))
                Directory.Delete(subDir, recursive: false); // remove link, not target
            else
                DeleteDirectoryRecursiveSafe(subDir);
        }

        foreach (var file in Directory.EnumerateFiles(dir))
            File.Delete(file);

        Directory.Delete(dir, recursive: false);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
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

            // NoProgressUI intentionally omitted — showing the shell's progress
            // dialog avoids the AV heuristic pattern of silent Recycle Bin emptying.
            Shell32Interop.SHEmptyRecycleBin(
                IntPtr.Zero, null,
                Shell32Interop.EmptyRecycleBinFlags.NoConfirmation |
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
