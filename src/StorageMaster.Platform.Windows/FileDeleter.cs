using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// Windows implementation of IFileDeleter.
/// Sends files to the Recycle Bin via SHFileOperation (FOF_ALLOWUNDO) with
/// FOF_NOERRORUI so no shell dialogs appear — errors become Win32Exception.
/// Falls back to permanent delete when the Recycle Bin is unavailable.
/// The sentinel path "::RecycleBin::" triggers SHEmptyRecycleBin instead.
/// </summary>
public sealed class FileDeleter : IFileDeleter
{
    private readonly ILogger<FileDeleter> _logger;

    // Limit concurrent deletions to avoid hammering a disk that is still being scanned.
    private const int MaxConcurrency = 4;

    public FileDeleter(ILogger<FileDeleter> logger) => _logger = logger;

    public async Task<DeletionOutcome> DeleteAsync(
        DeletionRequest   request,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield(); // ensure caller is never blocked synchronously

        if (request.DryRun)
        {
            long estimated = EstimateSize(request.Path);
            _logger.LogInformation("[DryRun] Would delete {Path} (~{Size} bytes)", request.Path, estimated);
            return new DeletionOutcome(request.Path, true, estimated);
        }

        // Special sentinel: empty the entire Recycle Bin.
        if (request.Path == "::RecycleBin::")
            return EmptyRecycleBin();

        try
        {
            long sizeBeforeDelete = EstimateSize(request.Path);

            if (request.Method == DeletionMethod.RecycleBin)
                DeleteToRecycleBin(request.Path);
            else
                DeletePermanently(request.Path);

            _logger.LogInformation("Deleted {Path} ({Size} bytes)", request.Path, sizeBeforeDelete);
            return new DeletionOutcome(request.Path, true, sizeBeforeDelete);
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
        // Use a semaphore to bound concurrency without requiring PLINQ.
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
                finally
                {
                    semaphore.Release();
                }
            });
            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                // Always complete the channel so ReadAllAsync doesn't hang if any
                // task faults unexpectedly (DeleteAsync catches per-file errors, but
                // defensive completion is still important for cancellation paths).
                channel.Writer.Complete();
            }
        }, cancellationToken);

        await foreach (var outcome in channel.Reader.ReadAllAsync(cancellationToken))
            yield return outcome;

        await producer; // propagate any unexpected exceptions
    }

    // ── Deletion helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Sends a file or folder to the Recycle Bin via SHFileOperation with
    /// FOF_NOERRORUI | FOF_NOCONFIRMATION | FOF_SILENT — no shell dialogs of any
    /// kind are shown. Errors are returned as Win32 exit codes and converted to
    /// exceptions so the caller's try/catch produces a failed DeletionOutcome.
    /// </summary>
    private static void DeleteToRecycleBin(string path)
    {
        // SHFileOperation requires a double-null-terminated source string.
        var op = new Shell32Interop.SHFILEOPSTRUCT
        {
            hwnd   = IntPtr.Zero,
            wFunc  = Shell32Interop.FO_DELETE,
            pFrom  = path + '\0',          // extra null appended; struct field string adds one more
            pTo    = null,
            fFlags = (ushort)(Shell32Interop.FOF_ALLOWUNDO |
                               Shell32Interop.FOF_NOCONFIRMATION |
                               Shell32Interop.FOF_NOERRORUI |
                               Shell32Interop.FOF_SILENT),
        };

        int result = Shell32Interop.SHFileOperation(ref op);
        if (result != 0)
            throw new Win32Exception(result,
                $"SHFileOperation failed with code 0x{result:X8} for path: {path}");
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
            // Query size before emptying so we can report bytes freed.
            var query = new Shell32Interop.SHQUERYRBINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Shell32Interop.SHQUERYRBINFO>()
            };
            Shell32Interop.SHQueryRecycleBin(null, ref query);
            long sizeFreed = query.i64Size;

            Shell32Interop.SHEmptyRecycleBin(
                IntPtr.Zero,
                null,
                Shell32Interop.EmptyRecycleBinFlags.NoConfirmation |
                Shell32Interop.EmptyRecycleBinFlags.NoProgressUI   |
                Shell32Interop.EmptyRecycleBinFlags.NoSound);

            _logger.LogInformation("Recycle Bin emptied. Freed {Size} bytes", sizeFreed);
            return new DeletionOutcome("::RecycleBin::", true, sizeFreed);
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
            if (File.Exists(path))
                return new FileInfo(path).Length;

            if (Directory.Exists(path))
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                    .Sum(f => f.Length);
        }
        catch { /* best-effort */ }
        return 0;
    }
}
