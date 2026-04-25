using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using StorageMaster.Core.Interfaces;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// Windows implementation of IFileDeleter.
/// Prefers sending files to the Recycle Bin using Microsoft.VisualBasic.FileIO
/// (which wraps SHFileOperation under the hood and supports undo).
/// Falls back to permanent delete when the Recycle Bin is unavailable (e.g. network drives).
///
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
            await Task.WhenAll(tasks);
            channel.Writer.Complete();
        }, cancellationToken);

        await foreach (var outcome in channel.Reader.ReadAllAsync(cancellationToken))
            yield return outcome;

        await producer; // propagate any unexpected exceptions
    }

    // ── Deletion helpers ──────────────────────────────────────────────────

    private static void DeleteToRecycleBin(string path)
    {
        if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
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
