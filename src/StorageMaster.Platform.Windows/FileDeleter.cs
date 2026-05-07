using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
        DeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (request.DryRun)
        {
            long est = EstimateSize(request.Path, cancellationToken);
            _logger.LogInformation("[DryRun] Would delete {Path} (~{Size} B)", request.Path, est);
            return new DeletionOutcome(request.Path, true, est);
        }

        if (request.Path == "::RecycleBin::")
            return EmptyRecycleBin();

        if (request.Path == "::DnsFlush::")
            return await FlushDnsCacheAsync();

        // Safety guard: refuse to operate on drive roots or UNC share roots
        // (e.g. "C:\", "\\server\share") — these would wipe entire volumes.
        if (IsRootOrUncPrefix(request.Path))
        {
            _logger.LogError("Refused to delete filesystem root or UNC prefix: {Path}", request.Path);
            return new DeletionOutcome(request.Path, false, 0,
                "Refusing to delete a filesystem root or UNC share prefix.");
        }

        try
        {
            long size = EstimateSize(request.Path, cancellationToken);

            if (request.Method == DeletionMethod.Quarantine)
                return await QuarantineAsync(request, size, cancellationToken);

            if (request.Method == DeletionMethod.RecycleBin)
                RecyclePathsViaIFileOperation([request.Path]);
            else
                DeletePermanently(request.Path);
            _logger.LogInformation("Deleted {Path} ({Size} B)", request.Path, size);
            return new DeletionOutcome(request.Path, true, size);
        }
        catch (OperationCanceledException)
        {
            throw;
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
        bool allRecycleBin = requests.All(r => !r.DryRun
                                                          && r.Method == DeletionMethod.RecycleBin
                                                          && r.Path != "::RecycleBin::"
                                                          && r.Path != "::DnsFlush::");
        // Quarantine is per-file; falls through to normal path
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
                    .Select(r => EstimateSize(r.Path, cancellationToken))
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
            // IFileOperation with FOF_NOERRORUI silently skips files it cannot recycle
            // (locked by another process, access denied, cross-volume Recycle Bin).
            // It returns S_OK and GetAnyOperationsAborted()=false even when items were skipped.
            // We verify each path is actually gone from its original location.
            // A file still present at its original path after PerformOperations means it failed.
            _logger.LogInformation("Batch recycle: {Count} item(s) submitted; verifying outcomes", requests.Count);
            int succeeded = 0, failed = 0;
            for (int i = 0; i < requests.Count; i++)
            {
                var path = requests[i].Path;
                bool recycled = !File.Exists(path) && !Directory.Exists(path);
                if (recycled)
                {
                    succeeded++;
                    yield return new DeletionOutcome(path, Success: true, BytesFreed: sizes[i]);
                }
                else
                {
                    failed++;
                    _logger.LogWarning("Batch recycle: item not moved to Recycle Bin (locked/denied?) — {Path}", path);
                    yield return new DeletionOutcome(path, Success: false, BytesFreed: 0,
                        Error: "File could not be moved to the Recycle Bin (may be locked, access denied, or cross-volume).");
                }
            }
            if (failed > 0)
                _logger.LogWarning("Batch recycle partial failure: {Succeeded} recycled, {Failed} skipped by IFileOperation", succeeded, failed);
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
        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var channel = System.Threading.Channels.Channel.CreateUnbounded<DeletionOutcome>();

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
            try { await Task.WhenAll(tasks); }
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
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            RecyclePathsViaIFileOperationCore(paths);
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RecyclePathsViaIFileOperationCore(paths);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            Name = "StorageMaster.RecycleBinSta",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RecyclePathsViaIFileOperationCore(IReadOnlyList<string> paths)
    {
        var fo = FileOperationInterop.CreateFileOperation();
        try
        {
            fo.SetOperationFlags(
                FileOperationInterop.FOF_ALLOWUNDO |   // send to Recycle Bin
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
            ClearReadOnly(path);
            File.Delete(path);
        }
    }

    /// <summary>
    /// Recursive directory delete that skips into reparse-point subdirectories,
    /// removing them as links only.
    ///
    /// Uses eager GetDirectories/GetFiles (not lazy Enumerate*) so that a folder
    /// vanishing mid-delete (race with the OS, another process, or Windows cleaning
    /// temp directories) produces a handled DirectoryNotFoundException rather than
    /// a partially-consumed enumerator exception.
    /// </summary>
    private static void DeleteDirectoryRecursiveSafe(string dir)
    {
        string[] subDirs;
        string[] files;
        try
        {
            subDirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (DirectoryNotFoundException) { return; } // dir vanished between scan and delete — treat as done

        foreach (var subDir in subDirs)
        {
            if (IsReparsePoint(subDir))
            {
                try { Directory.Delete(subDir, recursive: false); } // remove link, not target
                catch (DirectoryNotFoundException) { }              // link already gone
            }
            else
            {
                DeleteDirectoryRecursiveSafe(subDir);
            }
        }

        foreach (var file in files)
        {
            ClearReadOnly(file);
            try { File.Delete(file); }
            catch (FileNotFoundException) { } // deleted by another process between snapshot and delete
        }

        ClearReadOnly(dir);
        try { Directory.Delete(dir, recursive: false); }
        catch (DirectoryNotFoundException) { } // already removed by a parallel delete or OS cleanup
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> is a drive root (e.g. <c>C:\</c>)
    /// or a UNC share root (e.g. <c>\\server\share</c>) — paths that must never be passed
    /// to destructive operations because they represent entire volumes or shares.
    /// </summary>
    internal static bool IsRootOrUncPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        // Compare normalized (no trailing separator) so both "C:\" and "C:" are caught.
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootNorm = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalized, rootNorm, StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // Best-effort. The subsequent delete will return the actionable error.
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    // ── Quarantine ──────────────────────────────────────────────────────────

    private static readonly string QuarantineRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StorageMaster", "Quarantine");

    private async Task<DeletionOutcome> QuarantineAsync(
        DeletionRequest request,
        long size,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(request.Path))
        {
            const string message = "Directory quarantine is not supported; route folders through cleanup review or use Recycle Bin.";
            _logger.LogWarning("Blocked directory quarantine for {Path}", request.Path);
            return new DeletionOutcome(request.Path, false, 0, message);
        }

        var runId = request.QuarantineRunId ?? 0;
        var relative = MakeRelativeQuarantinePath(request.Path);
        var destination = Path.Combine(QuarantineRoot, runId.ToString(), relative);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Avoid collisions: append counter suffix if destination already exists.
            var dest = destination;
            var counter = 1;
            while (File.Exists(dest))
                dest = destination + $".{counter++}";

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(request.Path, dest);
            _logger.LogInformation("Quarantined {Source} → {Dest}", request.Path, dest);
            return new DeletionOutcome(request.Path, true, size, QuarantinePath: dest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to quarantine {Path}", request.Path);
            return new DeletionOutcome(request.Path, false, 0, ex.Message);
        }
    }

    private static string MakeRelativeQuarantinePath(string absolutePath)
    {
        // Strip drive letter/UNC prefix; replace ':' to keep valid path chars.
        var rooted = absolutePath.Replace(':', '_');
        if (Path.IsPathRooted(rooted))
            rooted = rooted.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rooted;
    }

    private async Task<DeletionOutcome> FlushDnsCacheAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            _logger.LogInformation("DNS cache flushed (exit code {Code})", proc.ExitCode);
            return new DeletionOutcome("::DnsFlush::", proc.ExitCode == 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush DNS cache");
            return new DeletionOutcome("::DnsFlush::", false, 0, ex.Message);
        }
    }

    private DeletionOutcome EmptyRecycleBin()
    {
        try
        {
            var query = new Shell32Interop.SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf<Shell32Interop.SHQUERYRBINFO>()
            };
            var queryHr = Shell32Interop.SHQueryRecycleBin(null, ref query);
            if (queryHr < 0)
                Marshal.ThrowExceptionForHR(queryHr);
            long freed = query.i64Size;

            // NoProgressUI intentionally omitted — showing the shell's progress
            // dialog avoids the AV heuristic pattern of silent Recycle Bin emptying.
            var emptyHr = Shell32Interop.SHEmptyRecycleBin(
                IntPtr.Zero, null,
                Shell32Interop.EmptyRecycleBinFlags.NoConfirmation |
                Shell32Interop.EmptyRecycleBinFlags.NoSound);
            var signedHr = unchecked((int)emptyHr);
            if (signedHr < 0)
                Marshal.ThrowExceptionForHR(signedHr);

            _logger.LogInformation("Recycle Bin emptied. Freed {Size} B", freed);
            return new DeletionOutcome("::RecycleBin::", true, freed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to empty Recycle Bin");
            return new DeletionOutcome("::RecycleBin::", false, 0, ex.Message);
        }
    }

    internal static long EstimateSize(string path, CancellationToken cancellationToken = default)
    {
        const int maxEntries = 100_000;
        var visited = 0;
        long total = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
                return new FileInfo(path).Length;

            if (!Directory.Exists(path))
                return 0;

            var stack = new Stack<string>();
            stack.Push(path);

            while (stack.Count > 0 && visited < maxEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                if (IsReparsePoint(dir))
                    continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (visited++ >= maxEntries)
                        return total;
                    if (IsReparsePoint(file))
                        continue;
                    try { total += new FileInfo(file).Length; }
                    catch { /* best-effort */ }
                }

                IEnumerable<string> dirs;
                try
                {
                    dirs = Directory.EnumerateDirectories(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var subDir in dirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (visited++ >= maxEntries)
                        return total;
                    if (!IsReparsePoint(subDir))
                        stack.Push(subDir);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch { /* best-effort */ }

        return total;
    }
}
