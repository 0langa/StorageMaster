using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

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
    private readonly IScanRepository    _repo;
    private readonly IScanErrorRepository? _errorRepo;
    private readonly IFileScanner       _fallback;
    private readonly ILogger<TurboFileScanner> _logger;

    private static readonly string BinaryPath = Path.Combine(
        AppContext.BaseDirectory, "turbo-scanner.exe");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TurboFileScanner(
        IScanRepository              repo,
        ILogger<TurboFileScanner>    logger,
        IFileScanner                 fallback,
        IScanErrorRepository?        errorRepo = null)
    {
        _repo      = repo;
        _logger    = logger;
        _fallback  = fallback;
        _errorRepo = errorRepo;
    }

    /// <summary>True when turbo-scanner.exe is present next to the application.</summary>
    public static bool IsAvailable => File.Exists(BinaryPath);

    public async Task<ScanSession> ScanAsync(
        ScanOptions             options,
        IProgress<ScanProgress> progress,
        CancellationToken       cancellationToken = default)
    {
        if (!IsAvailable)
        {
            _logger.LogWarning("turbo-scanner.exe not found at {Path}; falling back to managed scanner",
                BinaryPath);
            return await _fallback.ScanAsync(options, progress, cancellationToken);
        }

        _logger.LogInformation("Turbo scan starting at {Root}", options.RootPath);

        var session = await _repo.CreateSessionAsync(options.RootPath, cancellationToken);

        var psi = new ProcessStartInfo(BinaryPath)
        {
            ArgumentList        = { "--path", options.RootPath, "--threads", options.MaxParallelism.ToString() },
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute     = false,
            CreateNoWindow      = true,
            // Disable the default StreamReader wrapping — we create our own with a 1 MB buffer below.
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Pre-compute the exclusion list once so the producer hot-loop does no allocation per record.
        var sortedExclusions = options.ExcludedPaths
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            // Drain stderr in background so the process never blocks on a full pipe.
            var stderrTask = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                        _logger.LogDebug("[turbo-scanner] {Line}", line);
                }
                catch (OperationCanceledException) { /* process killed */ }
                catch (ObjectDisposedException)    { /* process disposed */ }
            }, CancellationToken.None);

            // ── Channel: decouples stdout reading from DB inserts ─────────────
            // Bounded at 2000 records so the producer can run ahead without
            // unbounded memory growth, while never blocking long enough for
            // the pipe buffer to fill (DB inserts are fast at batch size 500).
            var pipe = Channel.CreateBounded<TurboRecord>(new BoundedChannelOptions(2000)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode     = BoundedChannelFullMode.Wait,
            });

            long fileCount   = 0;
            long folderCount = 0;
            long totalBytes  = 0;
            string lastPath  = string.Empty;

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
                    while (await stdoutReader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        TurboRecord? rec;
                        try { rec = JsonSerializer.Deserialize<TurboRecord>(line, JsonOpts); }
                        catch { continue; }
                        if (rec is null) continue;

                        if (!options.DeepScan && IsExcluded(rec.Path, sortedExclusions)) continue;
                        if (!options.DeepScan && !options.IncludeHiddenFiles && IsHidden(rec.Path)) continue;

                        await pipe.Writer.WriteAsync(rec, cancellationToken);
                    }
                }
                finally
                {
                    pipe.Writer.Complete();
                }
            }, cancellationToken);

            // ── Consumer: batch + DB insert — runs concurrently with producer ─
            var fileBuffer   = new List<FileEntry>(500);
            var folderBuffer = new List<FolderEntry>(100);
            var parentSizes  = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var stopwatch    = Stopwatch.StartNew();

            var consumer = Task.Run(async () =>
            {
                await foreach (var rec in pipe.Reader.ReadAllAsync(cancellationToken))
                {
                    lastPath = rec.Path;

                    if (rec.IsDir)
                    {
                        var fe = new FolderEntry
                        {
                            Id              = 0,
                            SessionId       = session.Id,
                            FullPath        = rec.Path,
                            FolderName      = Path.GetFileName(rec.Path) ?? rec.Path,
                            DirectSizeBytes = 0,
                            TotalSizeBytes  = 0,
                            FileCount       = 0,
                            SubFolderCount  = 0,
                            IsReparsePoint  = false,
                            WasAccessDenied = false,
                        };
                        folderBuffer.Add(fe);
                        folderCount++;

                        if (folderBuffer.Count >= 100)
                        {
                            await _repo.UpsertFolderEntriesAsync([..folderBuffer], cancellationToken);
                            folderBuffer.Clear();
                        }
                    }
                    else
                    {
                        var ext       = Path.GetExtension(rec.Path);
                        var modUtc    = DateTimeOffset.FromUnixTimeSeconds(rec.ModifiedUnix).UtcDateTime;
                        var createUtc = DateTimeOffset.FromUnixTimeSeconds(rec.CreatedUnix).UtcDateTime;

                        var fe = new FileEntry
                        {
                            Id             = 0,
                            SessionId      = session.Id,
                            FullPath       = rec.Path,
                            FileName       = Path.GetFileName(rec.Path),
                            Extension      = ext,
                            SizeBytes      = (long)rec.Size,
                            CreatedUtc     = createUtc,
                            ModifiedUtc    = modUtc,
                            AccessedUtc    = modUtc,
                            Attributes     = FileAttributes.Normal,
                            Category       = FileTypeCategorizor.Categorize(ext),
                            IsReparsePoint = false,
                        };
                        fileBuffer.Add(fe);
                        fileCount++;
                        totalBytes += (long)rec.Size;

                        var parentDir = Path.GetDirectoryName(rec.Path);
                        if (parentDir is not null)
                            parentSizes[parentDir] = parentSizes.GetValueOrDefault(parentDir) + (long)rec.Size;

                        if (fileBuffer.Count >= 500)
                        {
                            await _repo.InsertFileEntriesAsync([..fileBuffer], cancellationToken);
                            fileBuffer.Clear();
                        }
                    }

                    // Report progress every ~300 ms.
                    if (stopwatch.ElapsedMilliseconds >= 300)
                    {
                        stopwatch.Restart();
                        progress.Report(new ScanProgress
                        {
                            CurrentPath    = lastPath,
                            FilesScanned   = fileCount,
                            FoldersScanned = folderCount,
                            BytesScanned   = totalBytes,
                            ErrorCount     = 0,
                            IsComplete     = false,
                        });
                    }
                }

                // Flush remaining buffers after the channel is drained.
                if (fileBuffer.Count   > 0) await _repo.InsertFileEntriesAsync([..fileBuffer], cancellationToken);
                if (folderBuffer.Count > 0) await _repo.UpsertFolderEntriesAsync([..folderBuffer], cancellationToken);

            }, cancellationToken);

            // Run producer and consumer concurrently — this is the key fix.
            await Task.WhenAll(producer, consumer);

            await process.WaitForExitAsync(CancellationToken.None);
            await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogError("turbo-scanner.exe exited with code {ExitCode}", process.ExitCode);

                var failed = session with
                {
                    Status         = ScanStatus.Failed,
                    CompletedUtc   = DateTime.UtcNow,
                    ErrorMessage   = $"turbo-scanner.exe exited with code {process.ExitCode}",
                    TotalFiles     = fileCount,
                    TotalFolders   = folderCount,
                    TotalSizeBytes = totalBytes,
                };
                await _repo.UpdateSessionAsync(failed, CancellationToken.None);
                return failed;
            }

            // Post-scan: aggregate folder totals bottom-up.
            progress.Report(new ScanProgress
            {
                CurrentPath    = "Finalizing: computing folder sizes…",
                FilesScanned   = fileCount,
                FoldersScanned = folderCount,
                BytesScanned   = totalBytes,
                ErrorCount     = 0,
                IsComplete     = false,
            });

            var allFolders = await _repo.GetAllFolderPathsForSessionAsync(session.Id, cancellationToken);
            var patchedFolders = allFolders
                .Select(f => f with { DirectSizeBytes = parentSizes.GetValueOrDefault(f.FullPath, 0L) })
                .ToList();
            var totals = FolderSizeAggregator.Compute(patchedFolders);
            await _repo.UpdateFolderTotalsAsync(session.Id, totals, cancellationToken);

            if (_errorRepo is not null)
            {
                // No errors list from Rust (errors go to stderr only); nothing to log here.
            }

            var completed = session with
            {
                Status         = ScanStatus.Completed,
                CompletedUtc   = DateTime.UtcNow,
                TotalFiles     = fileCount,
                TotalFolders   = folderCount,
                TotalSizeBytes = totalBytes,
            };
            await _repo.UpdateSessionAsync(completed, cancellationToken);

            progress.Report(new ScanProgress
            {
                CurrentPath    = lastPath,
                FilesScanned   = fileCount,
                FoldersScanned = folderCount,
                BytesScanned   = totalBytes,
                ErrorCount     = 0,
                IsComplete     = true,
            });

            _logger.LogInformation("Turbo scan {Id} complete. Files={F} Size={S}", session.Id, fileCount, totalBytes);
            return completed;
        }
        catch (OperationCanceledException)
        {
            await KillProcessSafelyAsync(process);

            var cancelled = session with
            {
                Status       = ScanStatus.Cancelled,
                CompletedUtc = DateTime.UtcNow,
            };
            await _repo.UpdateSessionAsync(cancelled, CancellationToken.None);
            _logger.LogWarning("Turbo scan {Id} cancelled — process killed", session.Id);
            return cancelled;
        }
        catch (Exception ex)
        {
            await KillProcessSafelyAsync(process);

            _logger.LogError(ex, "Turbo scan {Id} failed", session.Id);
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

    /// <summary>
    /// Kills the process if still running, awaits exit, and suppresses any
    /// errors from an already-exited process. Uses Kill(entireProcessTree: true)
    /// to clean up any child processes spawned by turbo-scanner.
    /// </summary>
    private static async Task KillProcessSafelyAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) { /* already exited */ }
        catch (SystemException) { /* process handle invalid */ }
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
            if (path.StartsWith(ex, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsHidden(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class TurboRecord
    {
        [JsonPropertyName("path")]          public string Path          { get; set; } = string.Empty;
        [JsonPropertyName("size")]          public ulong  Size          { get; set; }
        [JsonPropertyName("modified_unix")] public long   ModifiedUnix  { get; set; }
        [JsonPropertyName("created_unix")]  public long   CreatedUnix   { get; set; }
        [JsonPropertyName("is_dir")]        public bool   IsDir         { get; set; }
    }
}
