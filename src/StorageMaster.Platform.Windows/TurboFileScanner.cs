using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
///   C# spawns turbo-scanner.exe → reads JSONL from stdout line-by-line →
///   batches FileEntry objects → inserts to database → reports progress.
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
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stderr in background so the process never blocks on a full pipe.
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
                _logger.LogDebug("[turbo-scanner] {Line}", line);
        }, cancellationToken);

        long fileCount   = 0;
        long folderCount = 0;
        long totalBytes  = 0;
        string lastPath  = string.Empty;

        var fileBuffer   = new List<FileEntry>(500);
        var folderBuffer = new List<FolderEntry>(100);
        var errors       = new List<ScanError>();

        // Accumulates the sum of file sizes directly inside each folder path.
        // Used post-scan to populate DirectSizeBytes before running FolderSizeAggregator.
        var parentSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var stopwatch = Stopwatch.StartNew();

        await Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TurboRecord? rec;
                try { rec = JsonSerializer.Deserialize<TurboRecord>(line, JsonOpts); }
                catch { continue; }
                if (rec is null) continue;

                // Skip paths covered by the excluded list.
                if (!options.DeepScan && IsExcluded(rec.Path, options)) continue;

                lastPath = rec.Path;

                if (rec.IsDir)
                {
                    var fe = new FolderEntry
                    {
                        Id              = 0,
                        SessionId       = session.Id,
                        FullPath        = rec.Path,
                        FolderName      = Path.GetFileName(rec.Path) ?? rec.Path,
                        DirectSizeBytes = 0,   // patched post-scan via parentSizes
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
                        Id            = 0,
                        SessionId     = session.Id,
                        FullPath      = rec.Path,
                        FileName      = Path.GetFileName(rec.Path),
                        Extension     = ext,
                        SizeBytes     = (long)rec.Size,
                        CreatedUtc    = createUtc,
                        ModifiedUtc   = modUtc,
                        AccessedUtc   = modUtc,
                        Attributes    = FileAttributes.Normal,
                        Category      = FileTypeCategorizor.Categorize(ext),
                        IsReparsePoint = false,
                    };
                    fileBuffer.Add(fe);
                    fileCount++;
                    totalBytes += (long)rec.Size;

                    // Accumulate file size into its parent directory bucket.
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

            // Flush remaining buffers.
            if (fileBuffer.Count   > 0) await _repo.InsertFileEntriesAsync([..fileBuffer], cancellationToken);
            if (folderBuffer.Count > 0) await _repo.UpsertFolderEntriesAsync([..folderBuffer], cancellationToken);

        }, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await stderrTask;

        // Post-scan: aggregate folder totals bottom-up.
        // Patch DirectSizeBytes from the per-folder file-size accumulator so the
        // aggregator produces correct recursive totals (the turbo-scanner emits
        // directory entries with size=0 and file entries separately).
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

        if (errors.Count > 0 && _errorRepo is not null)
            await _errorRepo.LogErrorsAsync(session.Id, errors, cancellationToken);

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

    public IAsyncEnumerable<FileEntry> GetLargestFilesAsync(
        long sessionId, int topN = 100,
        CancellationToken cancellationToken = default)
        => _fallback.GetLargestFilesAsync(sessionId, topN, cancellationToken);

    public IAsyncEnumerable<FolderEntry> GetLargestFoldersAsync(
        long sessionId, int topN = 100,
        CancellationToken cancellationToken = default)
        => _fallback.GetLargestFoldersAsync(sessionId, topN, cancellationToken);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsExcluded(string path, ScanOptions options) =>
        options.ExcludedPaths.Any(ex =>
            path.StartsWith(ex, StringComparison.OrdinalIgnoreCase));

    private sealed class TurboRecord
    {
        [JsonPropertyName("path")]          public string Path          { get; set; } = string.Empty;
        [JsonPropertyName("size")]          public ulong  Size          { get; set; }
        [JsonPropertyName("modified_unix")] public long   ModifiedUnix  { get; set; }
        [JsonPropertyName("created_unix")]  public long   CreatedUnix   { get; set; }
        [JsonPropertyName("is_dir")]        public bool   IsDir         { get; set; }
    }
}
