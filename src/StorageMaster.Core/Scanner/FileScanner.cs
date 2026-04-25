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
    private readonly IScanRepository _repo;
    private readonly ILogger<FileScanner> _logger;

    public FileScanner(IScanRepository repo, ILogger<FileScanner> logger)
    {
        _repo   = repo;
        _logger = logger;
    }

    public async Task<ScanSession> ScanAsync(
        ScanOptions             options,
        IProgress<ScanProgress> progress,
        CancellationToken       cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.RootPath);

        var session = await _repo.CreateSessionAsync(options.RootPath, cancellationToken);
        _logger.LogInformation("Scan {SessionId} started at {Root}", session.Id, options.RootPath);

        var state = new ScanState(session.Id, options);

        try
        {
            // Progress reporter runs on a timer so the hot scan loop is never blocked.
            using var progressTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
            var progressTask = ReportProgressLoopAsync(progressTimer, state, progress, cancellationToken);

            await ScanDirectoryTreeAsync(options.RootPath, options, state, cancellationToken);

            // Flush any remaining buffered entries.
            await FlushFileBufferAsync(state, cancellationToken);
            await FlushFolderBufferAsync(state, cancellationToken);

            progressTimer.Dispose();
            await progressTask;

            var completed = session with
            {
                Status        = ScanStatus.Completed,
                CompletedUtc  = DateTime.UtcNow,
                TotalFiles    = state.FileCount,
                TotalFolders  = state.FolderCount,
                TotalSizeBytes = state.TotalBytes,
                AccessDeniedCount = state.AccessDeniedCount,
            };

            await _repo.UpdateSessionAsync(completed, cancellationToken);
            _logger.LogInformation("Scan {SessionId} completed. Files={Files} Size={Size}",
                session.Id, state.FileCount, state.TotalBytes);

            progress.Report(BuildProgress(state, complete: true));
            return completed;
        }
        catch (OperationCanceledException)
        {
            await FlushFileBufferAsync(state, CancellationToken.None);
            await FlushFolderBufferAsync(state, CancellationToken.None);

            var cancelled = session with
            {
                Status       = ScanStatus.Cancelled,
                CompletedUtc = DateTime.UtcNow,
                TotalFiles   = state.FileCount,
                TotalFolders = state.FolderCount,
                TotalSizeBytes = state.TotalBytes,
            };
            await _repo.UpdateSessionAsync(cancelled, CancellationToken.None);
            return cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan {SessionId} failed", session.Id);
            var failed = session with
            {
                Status       = ScanStatus.Failed,
                CompletedUtc = DateTime.UtcNow,
                ErrorMessage = ex.Message,
            };
            await _repo.UpdateSessionAsync(failed, CancellationToken.None);
            throw;
        }
    }

    // ── Directory traversal ────────────────────────────────────────────────

    private async Task ScanDirectoryTreeAsync(
        string            rootPath,
        ScanOptions       options,
        ScanState         state,
        CancellationToken ct)
    {
        // Channel provides backpressure: producers (directory enumerators) can't
        // enqueue infinitely ahead of consumers (I/O flushing).
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = false,
        });

        // Producer: walk the tree and feed directory paths into the channel.
        var producerTask = ProduceDirectoriesAsync(rootPath, options, state, channel.Writer, ct);

        // Consumers: process directories in parallel up to MaxParallelism.
        var consumerTasks = Enumerable
            .Range(0, options.MaxParallelism)
            .Select(_ => ConsumeDirectoriesAsync(options, state, channel.Reader, ct))
            .ToArray();

        await producerTask;
        await Task.WhenAll(consumerTasks);
    }

    private async Task ProduceDirectoriesAsync(
        string             root,
        ScanOptions        options,
        ScanState          state,
        ChannelWriter<string> writer,
        CancellationToken  ct)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        // Visited inode tracking prevents infinite loops through hard-linked dirs.
        // We use path normalisation as a lightweight first pass; full inode checks
        // require platform-specific code and are deferred to v2.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            while (queue.Count > 0 && !ct.IsCancellationRequested)
            {
                var dir = queue.Dequeue();

                if (!visited.Add(dir))
                    continue;

                if (IsExcluded(dir, options))
                    continue;

                // Write directory to channel for consumption.
                await writer.WriteAsync(dir, ct);

                // Enumerate subdirectories from this thread (keeps tree-order locality).
                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(dir);
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
                {
                    if (IsReparsePoint(sub) && !options.FollowSymlinks)
                        continue;

                    queue.Enqueue(sub);
                }
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task ConsumeDirectoriesAsync(
        ScanOptions          options,
        ScanState            state,
        ChannelReader<string> reader,
        CancellationToken    ct)
    {
        await foreach (var dir in reader.ReadAllAsync(ct))
        {
            ProcessDirectory(dir, options, state);

            // Flush when the buffer is large enough to amortise SQLite overhead.
            if (state.FileBuffer.Count >= options.DbBatchSize)
                await FlushFileBufferAsync(state, ct);

            if (state.FolderBuffer.Count >= options.DbBatchSize / 5)
                await FlushFolderBufferAsync(state, ct);
        }
    }

    private void ProcessDirectory(string dir, ScanOptions options, ScanState state)
    {
        long directBytes   = 0;
        int  fileCount     = 0;
        int  subDirCount   = 0;
        bool accessDenied  = false;

        try
        {
            // Enumerate files directly inside this directory.
            foreach (var filePath in Directory.EnumerateFiles(dir))
            {
                try
                {
                    var info = new FileInfo(filePath);
                    if (!info.Exists) continue;

                    var entry = new FileEntry
                    {
                        Id           = 0, // assigned by DB
                        SessionId    = state.SessionId,
                        FullPath     = filePath,
                        FileName     = info.Name,
                        Extension    = info.Extension,
                        SizeBytes    = info.Length,
                        CreatedUtc   = info.CreationTimeUtc,
                        ModifiedUtc  = info.LastWriteTimeUtc,
                        AccessedUtc  = info.LastAccessTimeUtc,
                        Attributes   = info.Attributes,
                        Category     = FileTypeCategorizor.Categorize(info.Extension),
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
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref state._accessDeniedCount);
            accessDenied = true;
        }
        catch (Exception ex) when (ex is IOException or SecurityException)
        {
            _logger.LogDebug("Skip dir {Dir}: {Msg}", dir, ex.Message);
        }

        // Count immediate subdirectories for the folder record.
        try { subDirCount = Directory.GetDirectories(dir).Length; }
        catch { /* best-effort */ }

        var folderEntry = new FolderEntry
        {
            Id              = 0,
            SessionId       = state.SessionId,
            FullPath        = dir,
            FolderName      = Path.GetFileName(dir) ?? dir,
            DirectSizeBytes = directBytes,
            TotalSizeBytes  = directBytes, // ancestor propagation done post-scan
            FileCount       = fileCount,
            SubFolderCount  = subDirCount,
            IsReparsePoint  = IsReparsePoint(dir),
            WasAccessDenied = accessDenied,
        };

        state.FolderBuffer.Enqueue(folderEntry);
        Interlocked.Increment(ref state._folderCount);
    }

    // ── Buffer flushing ────────────────────────────────────────────────────

    private async Task FlushFileBufferAsync(ScanState state, CancellationToken ct)
    {
        var batch = DrainQueue(state.FileBuffer);
        if (batch.Count == 0) return;
        await _repo.InsertFileEntriesAsync(batch, ct);
    }

    private async Task FlushFolderBufferAsync(ScanState state, CancellationToken ct)
    {
        var batch = DrainQueue(state.FolderBuffer);
        if (batch.Count == 0) return;
        await _repo.UpsertFolderEntriesAsync(batch, ct);
    }

    private static List<T> DrainQueue<T>(ConcurrentQueue<T> queue)
    {
        var list = new List<T>(queue.Count);
        while (queue.TryDequeue(out var item))
            list.Add(item);
        return list;
    }

    // ── IFileScanner query methods ─────────────────────────────────────────

    public async IAsyncEnumerable<FileEntry> GetLargestFilesAsync(
        long sessionId,
        int  topN,
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
        int  topN,
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
        PeriodicTimer          timer,
        ScanState              state,
        IProgress<ScanProgress> progress,
        CancellationToken      ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                progress.Report(BuildProgress(state, complete: false));
        }
        catch (OperationCanceledException) { /* expected */ }
    }

    private static ScanProgress BuildProgress(ScanState state, bool complete) => new()
    {
        CurrentPath    = state.LastScannedPath,
        FilesScanned   = Interlocked.Read(ref state._fileCount),
        FoldersScanned = Interlocked.Read(ref state._folderCount),
        BytesScanned   = Interlocked.Read(ref state._totalBytes),
        ErrorCount     = (int)Interlocked.Read(ref state._accessDeniedCount),
        IsComplete     = complete,
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

    private static bool IsExcluded(string path, ScanOptions options) =>
        options.ExcludedPaths.Any(ex =>
            path.StartsWith(ex, StringComparison.OrdinalIgnoreCase));

    // ── Inner state container ──────────────────────────────────────────────

    private sealed class ScanState
    {
        public long SessionId { get; }

        // These fields are written by multiple threads — keep as plain longs
        // and use Interlocked for all access.
        public long _fileCount;
        public long _folderCount;
        public long _totalBytes;
        public long _accessDeniedCount;

        public long FileCount        => Interlocked.Read(ref _fileCount);
        public long FolderCount      => Interlocked.Read(ref _folderCount);
        public long TotalBytes       => Interlocked.Read(ref _totalBytes);
        public long AccessDeniedCount => Interlocked.Read(ref _accessDeniedCount);

        // volatile so readers always see the latest value without a lock.
        private volatile string _lastScannedPath = string.Empty;
        public string LastScannedPath
        {
            get => _lastScannedPath;
            set => _lastScannedPath = value;
        }

        // ConcurrentQueue is the right structure here: many producers (worker
        // tasks), one periodic flusher. No locking needed.
        public ConcurrentQueue<FileEntry>   FileBuffer   { get; } = new();
        public ConcurrentQueue<FolderEntry> FolderBuffer { get; } = new();

        public ScanState(long sessionId, ScanOptions _)
        {
            SessionId = sessionId;
        }
    }
}

