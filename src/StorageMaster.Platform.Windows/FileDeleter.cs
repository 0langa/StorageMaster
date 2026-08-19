using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
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
///   • Snapshot-guarded requests take that same batch path. Validation is
///     split from execution: every path in a chunk has its ExpectedSnapshot
///     re-checked BEFORE any path in that chunk is submitted, so batching
///     changes how verified deletions are handed to the shell, never whether
///     they are verified. Chunking keeps the check→submit window short.
///   • On batch failure (partial error) the batch falls back to per-file mode
///     so individual error messages are captured — and the per-file path
///     re-validates each snapshot again before deleting.
///   • Permanent deletion uses parallel File.Delete / Directory.Delete.
///   • All IFileOperation work runs on ONE long-lived STA apartment thread
///     rather than a freshly created STA thread per call.
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
    private readonly IFileSnapshotProvider? _snapshotProvider;

    private const int MaxConcurrency = 8; // raised from 4; permanent deletes are lightweight

    // Verified requests are submitted to the shell in bounded chunks. One shell
    // transaction per 512 paths keeps ~all of the batching win over per-file
    // recycling while bounding both the snapshot-check→submit window and the
    // size of a single IFileOperation.
    private const int RecycleBatchSize = 512;

    /// <param name="VerifiedSize">
    /// Size read from the file handle during snapshot re-validation. Present only
    /// for snapshot-guarded requests; it lets the batch path skip a second stat.
    /// </param>
    private readonly record struct ValidatedDeletionRequest(
        DeletionRequest Request,
        string RequestedPath,
        long? VerifiedSize = null);

    public FileDeleter(
        ILogger<FileDeleter> logger,
        IFileSnapshotProvider? snapshotProvider = null)
    {
        _logger = logger;
        _snapshotProvider = snapshotProvider;
    }

    // ── Public interface ────────────────────────────────────────────────────

    public async Task<DeletionOutcome> DeleteAsync(
        DeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var requestedPath = request.Path ?? string.Empty;
        if (!TryNormalizeRequest(request, out var normalizedRequest, out var validationFailure))
        {
            _logger.LogError("Refused deletion request for {Path}: {Error}", request.Path, validationFailure.Error);
            return validationFailure;
        }
        request = normalizedRequest;

        var (snapshotRejection, verifiedSize) = await VerifyExpectedSnapshotAsync(
            request, requestedPath, cancellationToken).ConfigureAwait(false);
        if (snapshotRejection is not null)
            return snapshotRejection;

        if (request.DryRun)
        {
            long est = EstimateSize(request.Path, cancellationToken);
            _logger.LogInformation("[DryRun] Would delete {Path} (~{Size} B)", request.Path, est);
            return new DeletionOutcome(requestedPath, true, est);
        }

        if (request.Path == "::RecycleBin::" && request.Method != DeletionMethod.Permanent)
        {
            return new DeletionOutcome(
                requestedPath,
                false,
                0,
                "Emptying the Recycle Bin is irreversible and requires Permanent deletion mode.");
        }

        if (request.Path == "::RecycleBin::")
            return EmptyRecycleBin();

        if (request.Path == "::DnsFlush::")
            return await FlushDnsCacheAsync();

        try
        {
            // Snapshot re-validation already read the length off the open handle;
            // only unguarded requests still have to pay for a traversal here.
            long size = verifiedSize ?? EstimateSize(request.Path, cancellationToken);

            if (request.Method == DeletionMethod.Quarantine)
                return await QuarantineAsync(request, requestedPath, size, cancellationToken);

            if (request.Method == DeletionMethod.RecycleBin)
                RecyclePathsViaIFileOperation([request.Path]);
            else
                DeletePermanently(request.Path);
            _logger.LogInformation("Deleted {Path} ({Size} B)", request.Path, size);
            return new DeletionOutcome(requestedPath, true, size);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete {Path}", request.Path);
            return new DeletionOutcome(requestedPath, false, 0, ex.Message);
        }
    }

    public async IAsyncEnumerable<DeletionOutcome> DeleteManyAsync(
        IReadOnlyList<DeletionRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) yield break;

        var validRequests = new List<ValidatedDeletionRequest>(requests.Count);
        foreach (var request in requests)
        {
            if (!TryNormalizeRequest(request, out var normalizedRequest, out var validationFailure))
            {
                _logger.LogError("Refused deletion request for {Path}: {Error}", request.Path, validationFailure.Error);
                yield return validationFailure;
                continue;
            }

            validRequests.Add(new ValidatedDeletionRequest(
                normalizedRequest,
                request.Path ?? string.Empty));
        }

        if (validRequests.Count == 0) yield break;

        // ── Fast path: batch all RecycleBin real deletions in one shell call ──
        // This is the common cleanup case and is dramatically faster than N calls.
        //
        // Snapshot-guarded requests are deliberately eligible. Every production
        // caller sets ExpectedSnapshot, so excluding them left this path dead and
        // pushed real cleanups onto one STA thread + one IFileOperation per file.
        // The guard is not skipped: validation is simply hoisted ahead of
        // execution, chunk by chunk, below.
        bool allRecycleBin = validRequests.All(r => !r.Request.DryRun
                                                             && r.Request.Method == DeletionMethod.RecycleBin
                                                             && r.Request.Path != "::RecycleBin::"
                                                             && r.Request.Path != "::DnsFlush::");
        // Quarantine is per-file; falls through to normal path
        if (allRecycleBin && validRequests.Count > 1)
        {
            for (var start = 0; start < validRequests.Count; start += RecycleBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var end = Math.Min(start + RecycleBatchSize, validRequests.Count);
                var verified = new List<ValidatedDeletionRequest>(end - start);
                for (var i = start; i < end; i++)
                {
                    var candidate = validRequests[i];
                    var (rejection, verifiedSize) = await VerifyExpectedSnapshotAsync(
                        candidate.Request, candidate.RequestedPath, cancellationToken).ConfigureAwait(false);
                    if (rejection is not null)
                    {
                        yield return rejection;
                        continue;
                    }

                    verified.Add(candidate with { VerifiedSize = verifiedSize });
                }

                if (verified.Count == 0)
                    continue;

                await foreach (var o in BatchRecycleBinAsync(verified, cancellationToken))
                    yield return o;
            }

            yield break;
        }

        // ── Normal path: parallel per-file ───────────────────────────────────
        await foreach (var o in ParallelDeleteAsync(validRequests, cancellationToken))
            yield return o;
    }

    /// <summary>
    /// Re-checks a request's <c>ExpectedSnapshot</c> against the live file.
    /// Returns the refusal outcome when the file no longer matches, otherwise
    /// <c>null</c> plus the size read during the check.
    /// <para>
    /// This is the whole snapshot guarantee, so it runs for every guarded request
    /// on every path into the deleter — the batch path calls it before submitting
    /// anything, and the per-file path calls it again on the fallback route.
    /// </para>
    /// </summary>
    private async ValueTask<(DeletionOutcome? Rejection, long? VerifiedSize)> VerifyExpectedSnapshotAsync(
        DeletionRequest request,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        if (request.DryRun || request.ExpectedSnapshot is null)
            return (null, null);

        if (_snapshotProvider is null)
        {
            return (new DeletionOutcome(
                requestedPath,
                false,
                0,
                "Snapshot-guarded deletion is unavailable; refusing to delete."), null);
        }

        var currentSnapshot = await _snapshotProvider
            .TakeSnapshotAsync(request.Path, cancellationToken)
            .ConfigureAwait(false);
        if (currentSnapshot is null || !request.ExpectedSnapshot.IsIdenticalTo(currentSnapshot))
        {
            return (new DeletionOutcome(
                requestedPath,
                false,
                0,
                "File changed or was replaced after validation; refusing to delete."), null);
        }

        return (null, currentSnapshot.SizeBytes);
    }

    // ── Batch Recycle Bin (fast path) ───────────────────────────────────────

    private async IAsyncEnumerable<DeletionOutcome> BatchRecycleBinAsync(
        IReadOnlyList<ValidatedDeletionRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        // Measure sizes before deletion (best-effort, parallel for speed).
        // Guarded requests already carry the length their snapshot re-check read,
        // so the common cleanup batch needs no traversal at all here.
        List<long> sizes;
        if (requests.All(static r => r.VerifiedSize.HasValue))
        {
            sizes = requests.Select(static r => r.VerifiedSize!.Value).ToList();
        }
        else
        {
            sizes = await Task.Run(() =>
                requests.AsParallel().AsOrdered()
                        .Select(r => r.VerifiedSize ?? EstimateSize(r.Request.Path, cancellationToken))
                        .ToList(), cancellationToken);
        }

        var paths = requests.Select(r => r.Request.Path).ToList();
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
                var path = requests[i].Request.Path;
                var requestedPath = requests[i].RequestedPath;
                bool recycled = !File.Exists(path) && !Directory.Exists(path);
                if (recycled)
                {
                    succeeded++;
                    yield return new DeletionOutcome(requestedPath, Success: true, BytesFreed: sizes[i]);
                }
                else
                {
                    failed++;
                    _logger.LogWarning("Batch recycle: item not moved to Recycle Bin (locked/denied?) — {Path}", path);
                    yield return new DeletionOutcome(requestedPath, Success: false, BytesFreed: 0,
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
        IReadOnlyList<ValidatedDeletionRequest> requests,
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
                    var outcome = await DeleteAsync(req.Request, cancellationToken);
                    outcome = outcome with { Path = req.RequestedPath };
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
        // Already on an STA (including the shared apartment's own pump thread,
        // which keeps a re-entrant call from deadlocking on itself).
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            RecyclePathsViaIFileOperationCore(paths);
            return;
        }

        SharedStaApartment.Value.Invoke(() => RecyclePathsViaIFileOperationCore(paths));
    }

    private static readonly Lazy<StaApartment> SharedStaApartment =
        new(static () => new StaApartment(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// One long-lived STA thread that serialises all IFileOperation work.
    ///
    /// IFileOperation must be created and driven from an STA. Creating a thread
    /// and initialising a fresh apartment per call cost more than the shell
    /// operation itself on the per-file deletion path, so the apartment is
    /// created once and reused. It is a background thread, so it never keeps the
    /// process alive.
    /// </summary>
    private sealed class StaApartment
    {
        private readonly BlockingCollection<Action> _work = new();

        internal StaApartment()
        {
            var thread = new Thread(Pump)
            {
                Name = "StorageMaster.RecycleBinSta",
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Pump()
        {
            foreach (var item in _work.GetConsumingEnumerable())
                item();
        }

        internal void Invoke(Action action)
        {
            // A TaskCompletionSource rather than a disposable wait handle: the
            // pump signals completion from a different thread, and disposing a
            // handle the instant it is set races with the setter.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            // Rethrows the original exception with its stack preserved, matching
            // the ExceptionDispatchInfo behaviour of the former per-call thread.
            completion.Task.GetAwaiter().GetResult();
        }
    }

    private static void RecyclePathsViaIFileOperationCore(IReadOnlyList<string> paths)
    {
        var fo = FileOperationInterop.CreateFileOperation();
        try
        {
            fo.SetOperationFlags(FileOperationInterop.RecycleDeleteOperationFlags);

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
    /// recursively deleted. Every directory is locked and classified through
    /// a no-follow handle before enumeration, preventing namespace swaps from
    /// redirecting recursion outside the intended tree.
    /// </summary>
    internal static void DeletePermanently(string path) =>
        DeletePermanentlyCore(path, beforeDirectoryGuardOpen: null);

    /// <summary>Test seam invoked immediately before each no-follow directory open.</summary>
    internal static void DeletePermanently(
        string path,
        Action<string> beforeDirectoryGuardOpen) =>
        DeletePermanentlyCore(path, beforeDirectoryGuardOpen);

    private static void DeletePermanentlyCore(
        string path,
        Action<string>? beforeDirectoryGuardOpen)
    {
        if (Directory.Exists(path))
        {
            // Directory.Exists is only a type hint. The recursive routine reopens
            // and classifies the root itself through the same guarded path as every child.
            DeleteDirectoryRecursiveSafe(path, beforeDirectoryGuardOpen);
        }
        else
        {
            ClearReadOnly(path);
            File.Delete(path);
        }
    }

    /// <summary>
    /// Recursive directory delete that refuses to enumerate reparse-point directories,
    /// removing them as links only through the no-follow handle.
    ///
    /// The guard omits delete and write sharing, so the named directory cannot be
    /// renamed, deleted, or converted into a reparse point while its children are
    /// enumerated and deleted. It stays alive across all recursive child work.
    /// </summary>
    private static void DeleteDirectoryRecursiveSafe(
        string dir,
        Action<string>? beforeDirectoryGuardOpen)
    {
        beforeDirectoryGuardOpen?.Invoke(dir);
        using var guard = DirectoryTraversalInterop.TryOpenNoFollow(dir);
        if (guard is null)
            return;

        if (!guard.IsReparsePoint)
        {
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }

            foreach (var subDir in subDirs)
                DeleteDirectoryRecursiveSafe(subDir, beforeDirectoryGuardOpen);

            foreach (var file in files)
            {
                ClearReadOnly(file);
                try { File.Delete(file); }
                catch (FileNotFoundException) { } // entry vanished after enumeration
            }
        }

        // Delete the exact inspected object through its still-open no-follow handle.
        // This removes a reparse point as a link and avoids reopening a swappable path.
        guard.MarkForDeletion();
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> is a drive root (e.g. <c>C:\</c>)
    /// or a UNC share root (e.g. <c>\\server\share</c>) — paths that must never be passed
    /// to destructive operations because they represent entire volumes or shares.
    /// </summary>
    internal static bool IsRootOrUncPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathFullyQualified(path))
        {
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or NotSupportedException
                                       or PathTooLongException
                                       or SecurityException)
            {
                return false;
            }
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return false;
        // Compare normalized (no trailing separator) so both "C:\" and "C:" are caught.
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootNorm = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalized, rootNorm, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryNormalizeRequest(
        DeletionRequest request,
        out DeletionRequest normalizedRequest,
        out DeletionOutcome failure)
    {
        var requestedPath = request.Path ?? string.Empty;
        if (requestedPath is "::RecycleBin::" or "::DnsFlush::")
        {
            normalizedRequest = request;
            failure = default!;
            return true;
        }

        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            normalizedRequest = request;
            failure = new DeletionOutcome(requestedPath, false, 0,
                "Refusing to delete an empty filesystem path.");
            return false;
        }

        if (!Path.IsPathFullyQualified(requestedPath))
        {
            normalizedRequest = request;
            failure = new DeletionOutcome(requestedPath, false, 0,
                "Refusing to delete a relative or drive-relative path; an absolute filesystem path is required.");
            return false;
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or SecurityException)
        {
            normalizedRequest = request;
            failure = new DeletionOutcome(requestedPath, false, 0,
                $"Refusing to delete an invalid filesystem path: {ex.Message}");
            return false;
        }

        if (IsRootOrUncPrefix(canonicalPath))
        {
            normalizedRequest = request;
            failure = new DeletionOutcome(requestedPath, false, 0,
                "Refusing to delete a filesystem root or UNC share prefix.");
            return false;
        }

        normalizedRequest = request with { Path = canonicalPath };
        failure = default!;
        return true;
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

    private static readonly string QuarantineRoot = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StorageMaster", "Quarantine"));

    private async Task<DeletionOutcome> QuarantineAsync(
        DeletionRequest request,
        string requestedPath,
        long size,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(request.Path))
        {
            const string message = "Directory quarantine is not supported; route folders through cleanup review or use Recycle Bin.";
            _logger.LogWarning("Blocked directory quarantine for {Path}", request.Path);
            return new DeletionOutcome(requestedPath, false, 0, message);
        }

        var runId = request.QuarantineRunId ?? 0;
        var destination = GetCanonicalQuarantineDestination(request.Path, runId);

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
            return new DeletionOutcome(requestedPath, true, size, QuarantinePath: dest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to quarantine {Path}", request.Path);
            return new DeletionOutcome(requestedPath, false, 0, ex.Message);
        }
    }

    private static string GetCanonicalQuarantineDestination(string canonicalSourcePath, long runId)
    {
        var runRoot = Path.GetFullPath(Path.Combine(
            QuarantineRoot,
            runId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (!IsStrictChildPath(runRoot, QuarantineRoot))
            throw new IOException("The quarantine run directory escaped the quarantine root.");

        var relative = MakeRelativeQuarantinePath(canonicalSourcePath);
        var destination = Path.GetFullPath(Path.Combine(runRoot, relative));
        if (!IsStrictChildPath(destination, runRoot))
            throw new IOException("The quarantine destination escaped its quarantine run directory.");

        return destination;
    }

    private static bool IsStrictChildPath(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != "."
               && !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
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

    /// <summary>File metadata taken directly from the directory enumeration buffer.</summary>
    private readonly record struct EstimatedFile(long Length, FileAttributes Attributes);

    // Mirrors the options Directory.EnumerateFiles(path) uses, so which entries
    // the estimate sees is unchanged: nothing skipped by attribute, and access
    // failures surface rather than being silently swallowed mid-tree.
    private static readonly EnumerationOptions EstimateEnumerationOptions = new()
    {
        MatchType = MatchType.Win32,
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
    };

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

                // Length and attributes come straight out of the enumeration
                // buffer, so neither the size nor the reparse-point test costs a
                // stat per entry the way FileInfo.Length + File.GetAttributes did.
                IEnumerable<EstimatedFile> files;
                try
                {
                    files = new FileSystemEnumerable<EstimatedFile>(
                        dir,
                        static (ref FileSystemEntry entry) =>
                            new EstimatedFile(entry.Length, entry.Attributes),
                        EstimateEnumerationOptions)
                    {
                        ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
                    };
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
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    total += file.Length;
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
