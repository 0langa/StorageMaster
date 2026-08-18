using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scanner;

/// <summary>
/// Recursive, parallel, cancellation-aware file system scanner.
///
/// Performance design notes:
/// - Directory enumeration runs on a bounded pool (MaxParallelism) to avoid
///   overwhelming spinning hard drives with random seeks.
/// - File entries are collected in memory and flushed to the database in
///   configurable batches (ScanOptions.DbBatchSize) to amortise SQLite overhead.
/// - Folder sizes are accumulated in a ConcurrentDictionary keyed by path so
///   sibling and parent aggregation is lock-free at the file level.
/// - Symlinks and junctions are detected via FileAttributes.ReparsePoint and
///   skipped by default (FollowSymlinks = false) to prevent infinite loops.
/// - Progress is reported via a dedicated Channel so the hot path never blocks
///   waiting for UI marshalling.
/// </summary>
public sealed class FileScanner : IFileScanner
{
    private static readonly TimeSpan PartialFinalizationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TerminalUpdateTimeout = TimeSpan.FromSeconds(15);

    private readonly IScanRepository _repo;
    private readonly IScanErrorRepository? _errorRepo;
    private readonly IFileIdentityProvider _identityProvider;
    private readonly ILogger<FileScanner> _logger;

    public FileScanner(
        IScanRepository repo,
        ILogger<FileScanner> logger,
        IFileIdentityProvider identityProvider,
        IScanErrorRepository? errorRepo = null)
    {
        _repo = repo;
        _logger = logger;
        _identityProvider = identityProvider;
        _errorRepo = errorRepo;
    }

    public async Task<ScanSession> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken = default)
    {
        options = ScanOptionValidator.NormalizeAndValidate(options);

        var session = await _repo.CreateSessionAsync(options.RootPath, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Scan {SessionId} started at {Root}", session.Id, options.RootPath);

        var state = new ScanState(session.Id);

        // Declared outside try so catch blocks can await it for clean shutdown.
        var progressTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
        var progressTask = ReportProgressLoopAsync(progressTimer, state, progress, cancellationToken);

        try
        {
            if (!options.FollowSymlinks && IsReparsePoint(options.RootPath))
            {
                throw new InvalidOperationException(
                    $"Scan root is a reparse point and following links is disabled: {options.RootPath}");
            }

            progress.Report(BuildProgress(state, complete: false));

            await ScanDirectoryTreeAsync(options.RootPath, options, state, cancellationToken).ConfigureAwait(false);

            // Flush any remaining buffered entries.
            await FlushFileBufferAsync(state, cancellationToken).ConfigureAwait(false);
            await FlushFolderBufferAsync(state, cancellationToken).ConfigureAwait(false);

            // Post-scan: propagate folder sizes bottom-up so TotalSizeBytes is accurate.
            // Report an explicit status so the UI shows "Finalizing…" rather than appearing
            // frozen while we do the potentially expensive aggregation and DB write.
            state.LastScannedPath = "Finalizing: loading folder tree…";
            progress.Report(BuildProgress(state, complete: false));

            var allFolders = await _repo.GetAllFolderPathsForSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);

            state.LastScannedPath = "Finalizing: computing folder sizes…";
            progress.Report(BuildProgress(state, complete: false));

            var totals = FolderSizeAggregator.Compute(allFolders);

            state.LastScannedPath = "Finalizing: writing folder totals…";
            progress.Report(BuildProgress(state, complete: false));

            await _repo.UpdateFolderTotalsAsync(session.Id, totals, cancellationToken).ConfigureAwait(false);

            // Flush accumulated scan errors (access denied, I/O failures) if a repo is wired in.
            if (_errorRepo is not null)
                await FlushErrorBufferAsync(state, cancellationToken).ConfigureAwait(false);

            await StopProgressAsync(progressTimer, progressTask).ConfigureAwait(false);

            var completed = session with
            {
                Status = ScanStatus.Completed,
                CompletedUtc = DateTime.UtcNow,
                TotalFiles = state.PersistedFileCount,
                TotalFolders = state.PersistedFolderCount,
                TotalSizeBytes = state.PersistedBytes,
                AccessDeniedCount = state.AccessDeniedCount,
            };

            await _repo.UpdateSessionAsync(completed, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Scan {SessionId} completed. Files={Files} Size={Size}",
                session.Id, state.FileCount, state.TotalBytes);

            progress.Report(BuildProgress(state, complete: true));
            return completed;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await StopProgressAsync(progressTimer, progressTask).ConfigureAwait(false);

            try
            {
                using var finalization = new CancellationTokenSource(PartialFinalizationTimeout);
                await FinalizePartialScanAsync(session.Id, state, finalization.Token).ConfigureAwait(false);

                var cancelled = session with
                {
                    Status = ScanStatus.Cancelled,
                    CompletedUtc = DateTime.UtcNow,
                    TotalFiles = state.PersistedFileCount,
                    TotalFolders = state.PersistedFolderCount,
                    TotalSizeBytes = state.PersistedBytes,
                    AccessDeniedCount = state.AccessDeniedCount,
                };
                await _repo.UpdateSessionAsync(cancelled, finalization.Token).ConfigureAwait(false);
                return cancelled;
            }
            catch (Exception finalizationException)
            {
                _logger.LogError(finalizationException,
                    "Scan {SessionId} cancellation finalization failed", session.Id);

                var failed = session with
                {
                    Status = ScanStatus.Failed,
                    CompletedUtc = DateTime.UtcNow,
                    ErrorMessage = $"Cancellation finalization failed: {finalizationException.Message}",
                    TotalFiles = state.PersistedFileCount,
                    TotalFolders = state.PersistedFolderCount,
                    TotalSizeBytes = state.PersistedBytes,
                    AccessDeniedCount = state.AccessDeniedCount,
                };
                using var terminalUpdate = new CancellationTokenSource(TerminalUpdateTimeout);
                await _repo.UpdateSessionAsync(failed, terminalUpdate.Token).ConfigureAwait(false);
                return failed;
            }
        }
        catch (Exception ex)
        {
            await StopProgressAsync(progressTimer, progressTask).ConfigureAwait(false);

            _logger.LogError(ex, "Scan {SessionId} failed", session.Id);

            using var failureFinalization = new CancellationTokenSource(PartialFinalizationTimeout);
            try
            {
                await FinalizePartialScanAsync(session.Id, state, failureFinalization.Token).ConfigureAwait(false);
            }
            catch (Exception persistenceException)
            {
                _logger.LogError(persistenceException,
                    "Failed to persist partial scan {SessionId}", session.Id);
            }

            var failed = session with
            {
                Status = ScanStatus.Failed,
                CompletedUtc = DateTime.UtcNow,
                ErrorMessage = ex.Message,
                TotalFiles = state.PersistedFileCount,
                TotalFolders = state.PersistedFolderCount,
                TotalSizeBytes = state.PersistedBytes,
                AccessDeniedCount = state.AccessDeniedCount,
            };
            using var terminalUpdate = new CancellationTokenSource(TerminalUpdateTimeout);
            await _repo.UpdateSessionAsync(failed, terminalUpdate.Token).ConfigureAwait(false);
            throw;
        }
        finally
        {
            state.Dispose();
        }
    }

    // ── Directory traversal ────────────────────────────────────────────────

    private async Task ScanDirectoryTreeAsync(
        string rootPath,
        ScanOptions options,
        ScanState state,
        CancellationToken ct)
    {
        // Channel provides backpressure: producers (directory enumerators) can't
        // enqueue infinitely ahead of consumers (I/O flushing).
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false,
        });

        // If a consumer faults (e.g. a database failure) it stops reading; the
        // producer would then block forever on the bounded channel. The linked
        // token lets a failing consumer abort the producer so the real error
        // can propagate instead of deadlocking the scan.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Producer: walk the tree and feed directory paths into the channel.
        var producerTask = ProduceDirectoriesAsync(rootPath, options, state, channel.Writer, ct, abort.Token);

        // Consumers: process directories in parallel up to MaxParallelism.
        var consumerTasks = Enumerable
            .Range(0, options.MaxParallelism)
            .Select(async _ =>
            {
                try
                {
                    await ConsumeDirectoriesAsync(options, state, channel.Reader, ct).ConfigureAwait(false);
                }
                catch
                {
                    abort.Cancel();
                    throw;
                }
            })
            .ToArray();

        // Await everything together so the abort source stays alive until all
        // tasks have finished, and a consumer fault surfaces as the scan error.
        await Task.WhenAll([.. consumerTasks, producerTask]).ConfigureAwait(false);
    }

    private async Task ProduceDirectoriesAsync(
        string root,
        ScanOptions options,
        ScanState state,
        ChannelWriter<string> writer,
        CancellationToken externalCt,
        CancellationToken ct)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // In deep scan mode include hidden and system directories that .NET skips by default.
        var enumOptions = options.DeepScan
            ? new EnumerationOptions { AttributesToSkip = FileAttributes.None, IgnoreInaccessible = false }
            : new EnumerationOptions
            {
                AttributesToSkip = options.IncludeHiddenFiles ? FileAttributes.None : FileAttributes.Hidden,
                IgnoreInaccessible = true,
            };

        try
        {
            while (queue.Count > 0 && !ct.IsCancellationRequested)
            {
                var dir = queue.Dequeue();

                // In deep scan mode, skip the excluded-path list so everything is reachable.
                if (!options.DeepScan && IsExcluded(dir, options))
                    continue;

                if (!TryGetTraversalIdentity(dir, options.FollowSymlinks, out var identity))
                    continue;

                // Apply exclusions to the resolved target too. Otherwise a junction
                // alias could bypass a protected/excluded physical directory.
                if (!options.DeepScan && IsExcluded(identity, options))
                    continue;

                // Lexical paths are insufficient when following links: a junction
                // back to an ancestor produces a different path on every iteration.
                if (!visited.Add(identity))
                    continue;

                await writer.WriteAsync(dir, ct).ConfigureAwait(false);

                // EnumerateDirectories is lazy: with IgnoreInaccessible=false
                // (deep scan) access errors surface during iteration, not at the
                // call site — so the iteration itself must stay inside the try.
                var subDirs = new List<string>();
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir, "*", enumOptions))
                        subDirs.Add(sub);
                }
                catch (UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref state._accessDeniedCount);
                    continue;
                }
                catch (Exception ex) when (ex is IOException or SecurityException)
                {
                    _logger.LogDebug("Cannot enumerate {Dir}: {Msg}", dir, ex.Message);
                    continue;
                }

                foreach (var sub in subDirs)
                    queue.Enqueue(sub);
            }
        }
        catch (OperationCanceledException) when (!externalCt.IsCancellationRequested)
        {
            // A consumer faulted and aborted the producer; the consumer's own
            // exception is what should surface from the scan.
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task ConsumeDirectoriesAsync(
        ScanOptions options,
        ScanState state,
        ChannelReader<string> reader,
        CancellationToken ct)
    {
        await foreach (var dir in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // Revalidate immediately before enumeration. A directory can be
            // replaced by a junction after the producer first inspected it.
            if (!TryGetTraversalIdentity(dir, options.FollowSymlinks, out _))
                continue;

            await ProcessDirectoryAsync(dir, options, state, ct).ConfigureAwait(false);

            // Flush when the buffer is large enough to amortise SQLite overhead.
            if (state.FileBuffer.Count >= options.DbBatchSize)
                await FlushFileBufferAsync(state, ct).ConfigureAwait(false);

            if (state.FolderBuffer.Count >= Math.Max(1, options.DbBatchSize / 5))
                await FlushFolderBufferAsync(state, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessDirectoryAsync(
        string dir,
        ScanOptions options,
        ScanState state,
        CancellationToken ct)
    {
        long directBytes = 0;
        int fileCount = 0;
        int subDirCount = 0;
        bool accessDenied = false;

        // Deep scan: enumerate hidden and system files that .NET skips by default.
        var fileEnumOptions = options.DeepScan
            ? new EnumerationOptions { AttributesToSkip = FileAttributes.None, IgnoreInaccessible = false }
            : new EnumerationOptions
            {
                AttributesToSkip = options.IncludeHiddenFiles ? FileAttributes.None : FileAttributes.Hidden,
                IgnoreInaccessible = true,
            };

        try
        {
            // Enumerate files directly inside this directory.
            foreach (var filePath in Directory.EnumerateFiles(dir, "*", fileEnumOptions))
            {
                try
                {
                    // Capture identity before path-based metadata. If another
                    // process replaces the file after this handle closes, the
                    // persisted old identity makes later deletion fail closed.
                    var identity = await _identityProvider
                        .GetIdentityAsync(filePath, ct)
                        .ConfigureAwait(false);
                    var info = new FileInfo(filePath);
                    if (!info.Exists) continue;

                    var entry = new FileEntry
                    {
                        Id = 0, // assigned by DB
                        SessionId = state.SessionId,
                        FullPath = filePath,
                        FileName = info.Name,
                        Extension = info.Extension,
                        SizeBytes = info.Length,
                        CreatedUtc = info.CreationTimeUtc,
                        ModifiedUtc = info.LastWriteTimeUtc,
                        AccessedUtc = info.LastAccessTimeUtc,
                        Attributes = info.Attributes,
                        Category = FileTypeCategorizor.Categorize(info.Extension),
                        Identity = identity,
                        IsReparsePoint = info.Attributes.HasFlag(FileAttributes.ReparsePoint),
                    };

                    state.FileBuffer.Enqueue(entry);
                    directBytes += info.Length;
                    fileCount++;

                    Interlocked.Add(ref state._totalBytes, info.Length);
                    Interlocked.Increment(ref state._fileCount);
                    state.LastScannedPath = filePath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug("Skip file {Path}: {Msg}", filePath, ex.Message);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Interlocked.Increment(ref state._accessDeniedCount);
            accessDenied = true;
            state.ErrorBuffer.Enqueue(new ScanError
            {
                Id = 0,
                SessionId = state.SessionId,
                Path = dir,
                ErrorType = "UnauthorizedAccess",
                Message = ex.Message,
                OccurredAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex) when (ex is IOException or SecurityException)
        {
            _logger.LogDebug("Skip dir {Dir}: {Msg}", dir, ex.Message);

            // DirectoryNotFoundException means the folder disappeared during the scan
            // (a race condition with another process or Windows itself).  It is not a
            // user-actionable error — silently ignore it rather than showing it in the
            // Errors tab where it would only cause confusion.
            if (ex is DirectoryNotFoundException)
                return;

            state.ErrorBuffer.Enqueue(new ScanError
            {
                Id = 0,
                SessionId = state.SessionId,
                Path = dir,
                ErrorType = ex.GetType().Name,
                Message = ex.Message,
                OccurredAt = DateTime.UtcNow,
            });
        }

        // Count immediate subdirectories for the folder record (best-effort).
        try { subDirCount = Directory.GetDirectories(dir, "*", fileEnumOptions).Length; }
        catch { /* best-effort */ }

        var folderEntry = new FolderEntry
        {
            Id = 0,
            SessionId = state.SessionId,
            FullPath = dir,
            FolderName = ScanOptionValidator.GetDisplayName(dir),
            DirectSizeBytes = directBytes,
            TotalSizeBytes = directBytes, // ancestor propagation done post-scan
            FileCount = fileCount,
            SubFolderCount = subDirCount,
            IsReparsePoint = IsReparsePoint(dir),
            WasAccessDenied = accessDenied,
        };

        state.FolderBuffer.Enqueue(folderEntry);
        Interlocked.Increment(ref state._folderCount);
    }

    // ── Buffer flushing ────────────────────────────────────────────────────
    //
    // Multiple consumer tasks can hit the threshold simultaneously.
    // The SemaphoreSlim keeps one stable queue snapshot in flight. Entries are
    // removed only after the repository confirms the write, so cancellation or
    // a transient database failure cannot silently discard a drained batch.

    private async Task FlushFileBufferAsync(ScanState state, CancellationToken ct)
    {
        await state.FileFlushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = state.FileBuffer.ToArray();
            if (batch.Length == 0)
                return;

            await _repo.InsertFileEntriesAsync(batch, ct).ConfigureAwait(false);

            for (var i = 0; i < batch.Length; i++)
                state.FileBuffer.TryDequeue(out _);

            state.RecordPersistedFiles(batch);
        }
        finally
        {
            state.FileFlushLock.Release();
        }
    }

    private async Task FlushFolderBufferAsync(ScanState state, CancellationToken ct)
    {
        await state.FolderFlushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = state.FolderBuffer.ToArray();
            if (batch.Length == 0)
                return;

            await _repo.UpsertFolderEntriesAsync(batch, ct).ConfigureAwait(false);

            for (var i = 0; i < batch.Length; i++)
                state.FolderBuffer.TryDequeue(out _);

            state.RecordPersistedFolders(batch.Length);
        }
        finally
        {
            state.FolderFlushLock.Release();
        }
    }

    private async Task FlushErrorBufferAsync(ScanState state, CancellationToken ct)
    {
        if (_errorRepo is null)
            return;

        await state.ErrorFlushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = state.ErrorBuffer.ToArray();
            if (batch.Length == 0)
                return;

            await _errorRepo.LogErrorsAsync(state.SessionId, batch, ct).ConfigureAwait(false);

            for (var i = 0; i < batch.Length; i++)
                state.ErrorBuffer.TryDequeue(out _);
        }
        finally
        {
            state.ErrorFlushLock.Release();
        }
    }

    private async Task FinalizePartialScanAsync(long sessionId, ScanState state, CancellationToken ct)
    {
        await FlushFileBufferAsync(state, ct).ConfigureAwait(false);
        await FlushFolderBufferAsync(state, ct).ConfigureAwait(false);

        var folders = await _repo.GetAllFolderPathsForSessionAsync(sessionId, ct).ConfigureAwait(false);
        var totals = FolderSizeAggregator.Compute(folders);
        await _repo.UpdateFolderTotalsAsync(sessionId, totals, ct).ConfigureAwait(false);

        await FlushErrorBufferAsync(state, ct).ConfigureAwait(false);
    }

    // ── IFileScanner query methods ─────────────────────────────────────────

    public async IAsyncEnumerable<FileEntry> GetLargestFilesAsync(
        long sessionId,
        int topN,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var results = await _repo.GetLargestFilesAsync(sessionId, topN, cancellationToken);
        foreach (var f in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return f;
        }
    }

    public async IAsyncEnumerable<FolderEntry> GetLargestFoldersAsync(
        long sessionId,
        int topN,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var results = await _repo.GetLargestFoldersAsync(sessionId, topN, cancellationToken);
        foreach (var f in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return f;
        }
    }

    // ── Progress ──────────────────────────────────────────────────────────

    private static async Task ReportProgressLoopAsync(
        PeriodicTimer timer,
        ScanState state,
        IProgress<ScanProgress> progress,
        CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                progress.Report(BuildProgress(state, complete: false));
        }
        catch (OperationCanceledException) { /* expected */ }
    }

    private async Task StopProgressAsync(PeriodicTimer timer, Task progressTask)
    {
        timer.Dispose();
        try
        {
            await progressTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Progress is an observer. A faulty callback must not prevent the
            // scan's terminal session state from being persisted.
            _logger.LogWarning(ex, "Scan progress callback failed");
        }
    }

    private static ScanProgress BuildProgress(ScanState state, bool complete) => new()
    {
        CurrentPath = state.LastScannedPath,
        FilesScanned = Interlocked.Read(ref state._fileCount),
        FoldersScanned = Interlocked.Read(ref state._folderCount),
        BytesScanned = Interlocked.Read(ref state._totalBytes),
        ErrorCount = (int)Interlocked.Read(ref state._accessDeniedCount),
        IsComplete = complete,
    };

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    private bool TryGetTraversalIdentity(string path, bool followSymlinks, out string identity)
    {
        identity = string.Empty;

        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                identity = ScanOptionValidator.NormalizeDirectoryPath(directory.FullName);
                return true;
            }

            if (!followSymlinks)
                return false;

            var target = directory.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not DirectoryInfo targetDirectory || !targetDirectory.Exists)
            {
                _logger.LogDebug("Skip unresolved directory link {Path}", path);
                return false;
            }

            identity = ScanOptionValidator.NormalizeDirectoryPath(targetDirectory.FullName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                   or ArgumentException or NotSupportedException)
        {
            // Fail closed. Treat an unreadable/unresolvable link as unsafe to
            // traverse instead of falling back to its ever-growing lexical path.
            _logger.LogDebug("Cannot resolve directory identity {Path}: {Message}", path, ex.Message);
            return false;
        }
    }

    private static bool IsExcluded(string path, ScanOptions options) =>
        ScanOptionValidator.IsExcluded(path, options.ExcludedPaths);

    // ── Inner state container ──────────────────────────────────────────────

    private sealed class ScanState : IDisposable
    {
        public long SessionId { get; }

        // These fields are written by multiple threads — keep as plain longs
        // and use Interlocked for all access.
        public long _fileCount;
        public long _folderCount;
        public long _totalBytes;
        public long _accessDeniedCount;
        private long _persistedFileCount;
        private long _persistedFolderCount;
        private long _persistedBytes;

        public long FileCount => Interlocked.Read(ref _fileCount);
        public long FolderCount => Interlocked.Read(ref _folderCount);
        public long TotalBytes => Interlocked.Read(ref _totalBytes);
        public long AccessDeniedCount => Interlocked.Read(ref _accessDeniedCount);
        public long PersistedFileCount => Interlocked.Read(ref _persistedFileCount);
        public long PersistedFolderCount => Interlocked.Read(ref _persistedFolderCount);
        public long PersistedBytes => Interlocked.Read(ref _persistedBytes);

        // volatile so readers always see the latest value without a lock.
        private volatile string _lastScannedPath = string.Empty;
        public string LastScannedPath
        {
            get => _lastScannedPath;
            set => _lastScannedPath = value;
        }

        // ConcurrentQueue is the right structure here: many producers (worker
        // tasks), serialised flush via per-buffer SemaphoreSlim.
        public ConcurrentQueue<FileEntry> FileBuffer { get; } = new();
        public ConcurrentQueue<FolderEntry> FolderBuffer { get; } = new();
        public ConcurrentQueue<ScanError> ErrorBuffer { get; } = new();

        // Serialise buffer drains so concurrent consumers never double-drain.
        public SemaphoreSlim FileFlushLock { get; } = new(1, 1);
        public SemaphoreSlim FolderFlushLock { get; } = new(1, 1);
        public SemaphoreSlim ErrorFlushLock { get; } = new(1, 1);

        public ScanState(long sessionId)
        {
            SessionId = sessionId;
        }

        public void RecordPersistedFiles(IReadOnlyList<FileEntry> entries)
        {
            long bytes = 0;
            foreach (var entry in entries)
                bytes += entry.SizeBytes;

            Interlocked.Add(ref _persistedFileCount, entries.Count);
            Interlocked.Add(ref _persistedBytes, bytes);
        }

        public void RecordPersistedFolders(int count) =>
            Interlocked.Add(ref _persistedFolderCount, count);

        public void Dispose()
        {
            FileFlushLock.Dispose();
            FolderFlushLock.Dispose();
            ErrorFlushLock.Dispose();
        }
    }
}
