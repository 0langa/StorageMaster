using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// IFileScanner implementation that delegates enumeration to the native
/// Rust <c>turbo-scanner.exe</c> binary.
///
/// The Rust executable uses jwalk's work-stealing thread pool to walk the
/// directory tree in parallel across all CPU cores — significantly faster
/// than the managed FileScanner on multi-core systems with SSDs.
///
/// Data flow:
///   C# spawns turbo-scanner.exe → reads JSONL from stdout (1 MB buffer) →
///   producer writes to Channel → consumer batches + inserts to database.
///
/// The producer and consumer run concurrently so the stdout pipe is never
/// blocked waiting for a DB insert to finish. Previously, awaiting DB inserts
/// inside the read loop caused the 64 KB Windows pipe buffer to fill, stalling
/// the Rust process and negating its parallelism advantage.
///
/// Falls back gracefully to the managed FileScanner if the binary is not
/// found alongside the executable (e.g. during local F5 debug runs without
/// a published build).
/// </summary>
public sealed class TurboFileScanner : IFileScanner
{
    private readonly IScanRepository _repo;
    private readonly IScanErrorRepository? _errorRepo;
    private readonly IFileScanner _fallback;
    private readonly ILogger<TurboFileScanner> _logger;
    private readonly Func<ScanOptions, ProcessStartInfo>? _processStartInfoFactory;

    private static readonly string BinaryPath = Path.Combine(
        AppContext.BaseDirectory, "turbo-scanner.exe");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TurboFileScanner(
        IScanRepository repo,
        ILogger<TurboFileScanner> logger,
        IFileScanner fallback,
        IScanErrorRepository? errorRepo = null)
    {
        _repo = repo;
        _logger = logger;
        _fallback = fallback;
        _errorRepo = errorRepo;
    }

    internal TurboFileScanner(
        IScanRepository repo,
        ILogger<TurboFileScanner> logger,
        IFileScanner fallback,
        IScanErrorRepository? errorRepo,
        Func<ScanOptions, ProcessStartInfo> processStartInfoFactory)
        : this(repo, logger, fallback, errorRepo)
    {
        ArgumentNullException.ThrowIfNull(processStartInfoFactory);
        _processStartInfoFactory = processStartInfoFactory;
    }

    /// <summary>True when turbo-scanner.exe is present next to the application.</summary>
    public static bool IsAvailable => File.Exists(BinaryPath);

    public async Task<ScanSession> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken = default)
    {
        options = ScanOptionValidator.NormalizeAndValidate(options);

        if (_processStartInfoFactory is null && !IsAvailable)
        {
            _logger.LogWarning("turbo-scanner.exe not found at {Path}; falling back to managed scanner",
                BinaryPath);
            return await _fallback.ScanAsync(options, progress, cancellationToken);
        }

        // jwalk follows a root link even when descendant link following is disabled.
        // Validate every lexical root component through a no-follow handle. Where
        // ACL/sharing permits delete access, retain a no-delete-share handle so that
        // component cannot be swapped after validation and during native I/O.
        using var rootBoundaryLease = options.FollowSymlinks
            ? null
            : AcquireRootBoundaryLease(options.RootPath);
        if (rootBoundaryLease is { AllComponentsReplacementGuarded: false })
        {
            _logger.LogDebug(
                "One or more protected scan-root ancestors permit no-follow validation only; " +
                "their ACL or an existing handle prevented a delete-share lock");
        }

        _logger.LogInformation("Turbo scan starting at {Root}", options.RootPath);

        var session = await _repo.CreateSessionAsync(options.RootPath, cancellationToken);

        var psi = _processStartInfoFactory?.Invoke(options) ?? CreateDefaultProcessStartInfo(options);

        using var process = new Process { StartInfo = psi };

        // Pre-compute the exclusion list once so the producer hot-loop does no allocation per record.
        var sortedExclusions = options.ExcludedPaths.ToArray();
        var stderrErrors = new List<ScanError>();

        // Declared outside the try so the cancellation path can persist partial
        // totals (parity with the managed scanner).
        long fileCount = 0;
        long folderCount = 0;
        long totalBytes = 0;
        string lastPath = string.Empty;

        // Terminal session counts are based on successful repository writes,
        // not merely records observed on stdout.
        long persistedFileCount = 0;
        long persistedFolderCount = 0;
        long persistedBytes = 0;

        var fileBuffer = new List<FileEntry>(500);
        var folderBuffer = new List<FolderEntry>(100);
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var parentFileCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parentSubFolderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderMetricsPersisted = false;

        var processStarted = false;
        Task stderrTask = Task.CompletedTask;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenRegistration cancellationRegistration = default;

        async Task FlushFileBufferAsync(CancellationToken ct)
        {
            if (fileBuffer.Count == 0)
                return;

            var batch = fileBuffer.ToArray();
            await _repo.InsertFileEntriesAsync(batch, ct).ConfigureAwait(false);
            fileBuffer.Clear();
            persistedFileCount += batch.LongLength;
            persistedBytes += batch.Sum(static entry => entry.SizeBytes);
        }

        async Task FlushFolderBufferAsync(CancellationToken ct)
        {
            if (folderBuffer.Count == 0)
                return;

            var batch = folderBuffer.ToArray();
            await _repo.UpsertFolderEntriesAsync(batch, ct).ConfigureAwait(false);
            folderBuffer.Clear();
            persistedFolderCount += batch.LongLength;
        }

        async Task PersistFolderMetricsOnceAsync(CancellationToken ct)
        {
            if (folderMetricsPersisted)
                return;

            var metrics = seenFolders
                .Select(path =>
                {
                    var directSize = parentSizes.GetValueOrDefault(path);
                    return new FolderEntry
                    {
                        Id = 0,
                        SessionId = session.Id,
                        FullPath = path,
                        FolderName = ScanOptionValidator.GetDisplayName(path),
                        DirectSizeBytes = directSize,
                        TotalSizeBytes = directSize,
                        FileCount = parentFileCounts.GetValueOrDefault(path),
                        SubFolderCount = parentSubFolderCounts.GetValueOrDefault(path),
                        IsReparsePoint = false,
                        WasAccessDenied = false,
                    };
                })
                .ToArray();

            if (metrics.Length > 0)
                await _repo.UpsertFolderEntriesAsync(metrics, ct).ConfigureAwait(false);

            folderMetricsPersisted = true;
        }

        async Task FlushPersistedStateAsync(CancellationToken ct)
        {
            await FlushFileBufferAsync(ct).ConfigureAwait(false);
            await FlushFolderBufferAsync(ct).ConfigureAwait(false);
            await PersistFolderMetricsOnceAsync(ct).ConfigureAwait(false);
        }

        async Task UpdateFolderTotalsAsync(CancellationToken ct)
        {
            var allFolders = await _repo.GetAllFolderPathsForSessionAsync(session.Id, ct).ConfigureAwait(false);
            var totals = FolderSizeAggregator.Compute(allFolders);
            await _repo.UpdateFolderTotalsAsync(session.Id, totals, ct).ConfigureAwait(false);
        }

        static void Increment(Dictionary<string, int> counts, string path)
        {
            var current = counts.GetValueOrDefault(path);
            counts[path] = current == int.MaxValue ? int.MaxValue : current + 1;
        }

        try
        {
            process.Start();
            processStarted = true;
            cancellationRegistration = cancellationToken.Register(
                static state => TryKillProcess((Process)state!), process);

            // Drain stderr in background so the process never blocks on a full pipe.
            stderrTask = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync(abort.Token) is { } line)
                    {
                        _logger.LogDebug("[turbo-scanner] {Line}", line);
                        if (line.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                        {
                            lock (stderrErrors)
                            {
                                stderrErrors.Add(new ScanError
                                {
                                    Id = 0,
                                    SessionId = session.Id,
                                    Path = options.RootPath,
                                    ErrorType = "TurboScanner",
                                    Message = line,
                                    OccurredAt = DateTime.UtcNow,
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { /* process killed */ }
                catch (ObjectDisposedException) { /* process disposed */ }
            });

            // ── Channel: decouples stdout reading from DB inserts ─────────────
            // Bounded at 2000 records so the producer can run ahead without
            // unbounded memory growth, while never blocking long enough for
            // the pipe buffer to fill (DB inserts are fast at batch size 500).
            var pipe = Channel.CreateBounded<TurboRecord>(new BoundedChannelOptions(2000)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            // A consumer fault (e.g. database failure) stops channel reads; the
            // producer would then block forever on the bounded channel. The
            // linked token lets the failing consumer abort the producer.
            // ── Producer: read stdout at full speed, never awaits DB ──────────
            var producer = Task.Run(async () =>
            {
                // 1 MB StreamReader buffer keeps the pipe draining fast.
                // process.StandardOutput.BaseStream is the raw pipe — we wrap it
                // ourselves instead of using the 4 KB default StreamReader.
                using var stdoutReader = new StreamReader(
                    process.StandardOutput.BaseStream,
                    encoding: Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1_048_576,
                    leaveOpen: true);
                try
                {
                    while (await stdoutReader.ReadLineAsync(abort.Token) is { } line)
                    {
                        abort.Token.ThrowIfCancellationRequested();

                        TurboRecord? rec;
                        try { rec = JsonSerializer.Deserialize<TurboRecord>(line, JsonOpts); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Skipping malformed TurboScanner line: {Line}", line); continue; }
                        if (rec is null) continue;

                        if (!options.DeepScan && IsExcluded(rec.Path, sortedExclusions)) continue;
                        if (!options.DeepScan && !options.IncludeHiddenFiles && IsRecordHidden(rec)) continue;

                        await pipe.Writer.WriteAsync(rec, abort.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Consumer faulted and aborted the producer; the consumer's
                    // own exception is what should surface from the scan.
                }
                finally
                {
                    pipe.Writer.TryComplete();
                }
            });

            // ── Consumer: batch + DB insert — runs concurrently with producer ─
            var stopwatch = Stopwatch.StartNew();

            var consumer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var rec in pipe.Reader.ReadAllAsync(abort.Token))
                    {
                        lastPath = rec.Path;

                        if (rec.IsDir)
                        {
                            if (!seenFolders.Add(rec.Path))
                                continue;

                            var fe = new FolderEntry
                            {
                                Id = 0,
                                SessionId = session.Id,
                                FullPath = rec.Path,
                                FolderName = ScanOptionValidator.GetDisplayName(rec.Path),
                                DirectSizeBytes = 0,
                                TotalSizeBytes = 0,
                                FileCount = 0,
                                SubFolderCount = 0,
                                IsReparsePoint = HasAttribute(rec, FileAttributes.ReparsePoint),
                                WasAccessDenied = false,
                            };
                            folderBuffer.Add(fe);
                            folderCount++;

                            var parentDir = Path.GetDirectoryName(rec.Path);
                            if (parentDir is not null)
                                Increment(parentSubFolderCounts, parentDir);

                            if (folderBuffer.Count >= 100)
                                await FlushFolderBufferAsync(abort.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            var ext = Path.GetExtension(rec.Path);
                            var modUtc = ReadUtcTimestamp(rec.ModifiedUtcTicks, rec.ModifiedUnix);
                            var createUtc = ReadUtcTimestamp(rec.CreatedUtcTicks, rec.CreatedUnix);
                            var attributes = ReadAttributes(rec);

                            var fe = new FileEntry
                            {
                                Id = 0,
                                SessionId = session.Id,
                                FullPath = rec.Path,
                                FileName = Path.GetFileName(rec.Path),
                                Extension = ext,
                                SizeBytes = (long)rec.Size,
                                CreatedUtc = createUtc,
                                ModifiedUtc = modUtc,
                                AccessedUtc = modUtc,
                                Attributes = attributes,
                                Category = FileTypeCategorizor.Categorize(ext),
                                Identity = ReadIdentity(rec),
                                IsReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0,
                            };
                            fileBuffer.Add(fe);
                            fileCount++;
                            totalBytes += (long)rec.Size;

                            var parentDir = Path.GetDirectoryName(rec.Path);
                            if (parentDir is not null)
                            {
                                parentSizes[parentDir] = parentSizes.GetValueOrDefault(parentDir) + (long)rec.Size;
                                Increment(parentFileCounts, parentDir);
                            }

                            if (fileBuffer.Count >= 500)
                                await FlushFileBufferAsync(abort.Token).ConfigureAwait(false);
                        }

                        // Report progress every ~300 ms.
                        if (stopwatch.ElapsedMilliseconds >= 300)
                        {
                            stopwatch.Restart();
                            progress.Report(new ScanProgress
                            {
                                CurrentPath = lastPath,
                                FilesScanned = fileCount,
                                FoldersScanned = folderCount,
                                BytesScanned = totalBytes,
                                ErrorCount = 0,
                                IsComplete = false,
                            });
                        }
                    }

                    // Flush remaining buffers after the channel is drained.
                    await FlushFileBufferAsync(abort.Token).ConfigureAwait(false);
                    await FlushFolderBufferAsync(abort.Token).ConfigureAwait(false);
                }
                catch
                {
                    abort.Cancel();
                    throw;
                }
            });

            // Run producer and consumer concurrently — this is the key fix.
            await Task.WhenAll(producer, consumer);

            // A native process can close stdout and remain alive. Continue to
            // honor caller cancellation while waiting for its actual exit.
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stderrTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Folder identity rows are streamed as zero-value placeholders.
            // Persist their final direct metrics exactly once before any
            // terminal session state is written.
            await FlushPersistedStateAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogError("turbo-scanner.exe exited with code {ExitCode}", process.ExitCode);

                var failed = session with
                {
                    Status = ScanStatus.Failed,
                    CompletedUtc = DateTime.UtcNow,
                    ErrorMessage = $"turbo-scanner.exe exited with code {process.ExitCode}",
                    TotalFiles = persistedFileCount,
                    TotalFolders = persistedFolderCount,
                    TotalSizeBytes = persistedBytes,
                };
                await UpdateFolderTotalsAsync(cancellationToken).ConfigureAwait(false);
                await _repo.UpdateSessionAsync(failed, cancellationToken).ConfigureAwait(false);
                return failed;
            }

            // Post-scan: aggregate folder totals bottom-up.
            progress.Report(new ScanProgress
            {
                CurrentPath = "Finalizing: computing folder sizes…",
                FilesScanned = fileCount,
                FoldersScanned = folderCount,
                BytesScanned = totalBytes,
                ErrorCount = 0,
                IsComplete = false,
            });

            await UpdateFolderTotalsAsync(cancellationToken).ConfigureAwait(false);

            if (_errorRepo is not null)
            {
                List<ScanError> errors;
                lock (stderrErrors)
                    errors = [.. stderrErrors];

                if (errors.Count > 0)
                    await _errorRepo.LogErrorsAsync(session.Id, errors, cancellationToken).ConfigureAwait(false);
            }

            var completed = session with
            {
                Status = ScanStatus.Completed,
                CompletedUtc = DateTime.UtcNow,
                TotalFiles = persistedFileCount,
                TotalFolders = persistedFolderCount,
                TotalSizeBytes = persistedBytes,
            };
            await _repo.UpdateSessionAsync(completed, cancellationToken).ConfigureAwait(false);

            progress.Report(new ScanProgress
            {
                CurrentPath = lastPath,
                FilesScanned = fileCount,
                FoldersScanned = folderCount,
                BytesScanned = totalBytes,
                ErrorCount = stderrErrors.Count,
                IsComplete = true,
            });

            _logger.LogInformation("Turbo scan {Id} complete. Files={F} Size={S}", session.Id, fileCount, totalBytes);
            return completed;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            abort.Cancel();
            try
            {
                await TerminateProcessAsync(process, processStarted).ConfigureAwait(false);
                await ObserveTaskAfterProcessExitAsync(stderrTask).ConfigureAwait(false);

                using var finalization = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await FlushPersistedStateAsync(finalization.Token).ConfigureAwait(false);
                await UpdateFolderTotalsAsync(finalization.Token).ConfigureAwait(false);

                var cancelled = session with
                {
                    Status = ScanStatus.Cancelled,
                    CompletedUtc = DateTime.UtcNow,
                    TotalFiles = persistedFileCount,
                    TotalFolders = persistedFolderCount,
                    TotalSizeBytes = persistedBytes,
                };
                await _repo.UpdateSessionAsync(cancelled, finalization.Token).ConfigureAwait(false);
                _logger.LogWarning("Turbo scan {Id} cancelled — process terminated", session.Id);
                return cancelled;
            }
            catch (Exception finalizationException)
            {
                _logger.LogError(finalizationException,
                    "Turbo scan {Id} cancellation finalization failed", session.Id);

                using var terminalUpdate = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var failed = session with
                {
                    Status = ScanStatus.Failed,
                    CompletedUtc = DateTime.UtcNow,
                    ErrorMessage = $"Cancellation finalization failed: {finalizationException.Message}",
                    TotalFiles = persistedFileCount,
                    TotalFolders = persistedFolderCount,
                    TotalSizeBytes = persistedBytes,
                };
                await _repo.UpdateSessionAsync(failed, terminalUpdate.Token).ConfigureAwait(false);
                return failed;
            }
        }
        catch (Exception ex)
        {
            abort.Cancel();
            try
            {
                await TerminateProcessAsync(process, processStarted).ConfigureAwait(false);
                await ObserveTaskAfterProcessExitAsync(stderrTask).ConfigureAwait(false);
            }
            catch (Exception terminationException)
            {
                _logger.LogError(terminationException,
                    "Failed to terminate turbo-scanner process for scan {Id}", session.Id);
            }

            _logger.LogError(ex, "Turbo scan {Id} failed", session.Id);

            using var failureFinalization = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await FlushPersistedStateAsync(failureFinalization.Token).ConfigureAwait(false);
                await UpdateFolderTotalsAsync(failureFinalization.Token).ConfigureAwait(false);
            }
            catch (Exception persistenceException)
            {
                _logger.LogError(persistenceException,
                    "Failed to persist partial turbo scan {Id}", session.Id);
            }

            var failed = session with
            {
                Status = ScanStatus.Failed,
                CompletedUtc = DateTime.UtcNow,
                ErrorMessage = ex.Message,
                TotalFiles = persistedFileCount,
                TotalFolders = persistedFolderCount,
                TotalSizeBytes = persistedBytes,
            };
            using var terminalUpdate = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _repo.UpdateSessionAsync(failed, terminalUpdate.Token).ConfigureAwait(false);
            throw;
        }
        finally
        {
            cancellationRegistration.Dispose();
            abort.Cancel();
        }
    }

    internal static ProcessStartInfo CreateDefaultProcessStartInfo(ScanOptions options)
    {
        var psi = new ProcessStartInfo(BinaryPath)
        {
            // --skip-hidden prunes hidden files AND hidden directory subtrees
            // during native enumeration (contract v2), matching the managed
            // scanner's EnumerationOptions.AttributesToSkip semantics.
            ArgumentList =
            {
                "--path", options.RootPath,
                "--threads", options.MaxParallelism.ToString(CultureInfo.InvariantCulture),
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!options.DeepScan && !options.IncludeHiddenFiles)
            psi.ArgumentList.Add("--skip-hidden");

        if (options.FollowSymlinks)
            psi.ArgumentList.Add("--follow-links");

        return psi;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* not started or already exited */ }
        catch (SystemException) { /* final awaited termination reports failure */ }
    }

    private static async Task TerminateProcessAsync(Process process, bool processStarted)
    {
        if (!processStarted)
            return;

        TryKillProcess(process);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Process was already exited or its handle has been released.
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("turbo-scanner.exe did not exit after termination.");
        }
    }

    private static async Task ObserveTaskAfterProcessExitAsync(Task task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // Killing the native process closes redirected pipes abruptly.
        }
    }

    public IAsyncEnumerable<FileEntry> GetLargestFilesAsync(
        long sessionId, int topN = 100,
        CancellationToken cancellationToken = default)
        => _fallback.GetLargestFilesAsync(sessionId, topN, cancellationToken);

    public IAsyncEnumerable<FolderEntry> GetLargestFoldersAsync(
        long sessionId, int topN = 100,
        CancellationToken cancellationToken = default)
        => _fallback.GetLargestFoldersAsync(sessionId, topN, cancellationToken);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether <paramref name="path"/> starts with any of the sorted
    /// exclusion prefixes. Called on the producer hot-loop — kept allocation-free.
    /// </summary>
    private static bool IsExcluded(string path, string[] sortedExclusions)
    {
        foreach (var ex in sortedExclusions)
        {
            if (ScanOptionValidator.IsPathEqualOrUnder(path, ex))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Contract v2 binaries emit is_hidden and prune hidden subtrees natively
    /// when --skip-hidden is passed — no syscall needed here. A null IsHidden
    /// means a v1 binary is paired with this build (partial upgrade / stale
    /// exe); fall back to the per-file attribute check so hidden files cannot
    /// silently leak into results.
    /// </summary>
    private static bool IsRecordHidden(TurboRecord rec)
    {
        if (rec.Attributes is { } attributes)
            return (unchecked((FileAttributes)attributes) & FileAttributes.Hidden) != 0;

        if (rec.IsHidden is { } hidden)
            return hidden;

        try
        {
            return (File.GetAttributes(rec.Path) & FileAttributes.Hidden) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasAttribute(TurboRecord rec, FileAttributes attribute) =>
        (ReadAttributes(rec) & attribute) != 0;

    private static FileAttributes ReadAttributes(TurboRecord rec) =>
        rec.Attributes is { } attributes
            ? unchecked((FileAttributes)attributes)
            : rec.IsHidden == true
                ? FileAttributes.Hidden
                : FileAttributes.Normal;

    private static DateTime ReadUtcTimestamp(long? exactTicks, long legacyUnixSeconds)
    {
        if (exactTicks is { } ticks)
        {
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                throw new JsonException($"Turbo scanner emitted invalid UTC ticks: {ticks}.");

            return new DateTime(ticks, DateTimeKind.Utc);
        }

        return DateTimeOffset.FromUnixTimeSeconds(legacyUnixSeconds).UtcDateTime;
    }

    private static FileIdentity? ReadIdentity(TurboRecord rec) =>
        rec.VolumeSerial is { } volumeSerial && rec.FileIndex is { } fileIndex
            ? new FileIdentity(
                volumeSerial.ToString("X8", CultureInfo.InvariantCulture),
                fileIndex)
            : null;

    private static RootBoundaryLease AcquireRootBoundaryLease(string rootPath)
    {
        var paths = new Stack<string>();
        for (var current = new DirectoryInfo(rootPath); current is not null; current = current.Parent)
            paths.Push(current.FullName);

        var guards = new List<DirectoryReadTraversalGuard>(paths.Count);
        try
        {
            while (paths.TryPop(out var path))
            {
                var guard = DirectoryTraversalInterop.TryOpenNoFollowForReadTraversal(path)
                    ?? throw new DirectoryNotFoundException(
                        $"Scan root ancestry disappeared before native traversal: {path}");
                if (guard.IsReparsePoint)
                {
                    guard.Dispose();
                    throw new InvalidOperationException(
                        $"Scan root ancestry contains a reparse point and following links is disabled: {path}");
                }

                guards.Add(guard);
            }

            return new RootBoundaryLease(guards);
        }
        catch
        {
            for (var index = guards.Count - 1; index >= 0; index--)
                guards[index].Dispose();
            throw;
        }
    }

    private sealed class TurboRecord
    {
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("size")] public ulong Size { get; set; }
        [JsonPropertyName("modified_unix")] public long ModifiedUnix { get; set; }
        [JsonPropertyName("created_unix")] public long CreatedUnix { get; set; }
        [JsonPropertyName("modified_utc_ticks")] public long? ModifiedUtcTicks { get; set; }
        [JsonPropertyName("created_utc_ticks")] public long? CreatedUtcTicks { get; set; }
        [JsonPropertyName("attributes")] public uint? Attributes { get; set; }
        [JsonPropertyName("volume_serial")] public uint? VolumeSerial { get; set; }
        [JsonPropertyName("file_index")] public ulong? FileIndex { get; set; }
        [JsonPropertyName("is_dir")] public bool IsDir { get; set; }
        // Contract v2; null when a v1 binary produced the record.
        [JsonPropertyName("is_hidden")] public bool? IsHidden { get; set; }
    }

    private sealed class RootBoundaryLease(IReadOnlyList<DirectoryReadTraversalGuard> guards) : IDisposable
    {
        internal bool AllComponentsReplacementGuarded =>
            guards.All(static guard => guard.BlocksReplacement);

        public void Dispose()
        {
            for (var index = guards.Count - 1; index >= 0; index--)
                guards[index].Dispose();
        }
    }
}
