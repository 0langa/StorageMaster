using System.Diagnostics;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Runs a deep scan in a short-lived elevated worker and reports its progress back
/// into the app.
/// <para>
/// Deep scanning needs administrator rights to read protected folders. The window
/// stays unelevated: only the scan is elevated, only for as long as it runs, and
/// the channel back is one-way, so nothing the UI does can direct the elevated
/// process beyond the arguments it was started with.
/// </para>
/// <para>
/// The previous behaviour launched a detached CLI process. It worked, but the user
/// watched a console window they could not follow and the app showed nothing until
/// they refreshed by hand.
/// </para>
/// </summary>
public sealed class ElevatedScanRunner(IAdminService adminService)
{
    /// <summary>Outcome of an elevated scan attempt.</summary>
    public sealed record Result(bool Started, bool Completed, long? SessionId, string? Status, string? Error)
    {
        public static Result Declined() => new(false, false, null, null, null);

        public static Result Failed(string error) => new(true, false, null, nameof(ScanStatus.Failed), error);
    }

    /// <summary>
    /// Starts the worker and pumps progress until it reports completion or exits.
    /// <para>
    /// <paramref name="onProgress"/> is invoked on a thread-pool thread; callers on
    /// the UI thread must marshal.
    /// </para>
    /// </summary>
    public async Task<Result> RunAsync(
        string path,
        bool useTurboScanner,
        Action<ElevatedScanProgressReport> onProgress,
        CancellationToken ct = default)
    {
        // Created here, unelevated, so the file belongs to the UI's own identity and
        // stays readable no matter what the elevated child does to it.
        var progressPath = Path.Combine(
            Path.GetTempPath(),
            $"storagemaster-scan-{Guid.NewGuid():N}.jsonl");

        try
        {
            await File.WriteAllTextAsync(progressPath, string.Empty, ct).ConfigureAwait(false);

            // --headless rather than --cli: it attaches to a console only if one
            // already exists, so the worker runs without flashing a window.
            var arguments = CommandLineArguments.Join(
                "--headless", "scan",
                "--path", path,
                "--deep",
                "--progress", progressPath);

            if (useTurboScanner)
                arguments += " --turbo";

            if (!adminService.TryStartElevated(arguments))
                return Result.Declined();

            return await PumpAsync(progressPath, onProgress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failed(ex.Message);
        }
        finally
        {
            TryDelete(progressPath);
        }
    }

    /// <summary>
    /// Reads the channel until the worker writes its terminal line.
    /// <para>
    /// Polls rather than watches: the writer is a different process at a higher
    /// integrity level, and a poll of a file we own is both simpler and immune to
    /// missed change notifications on a busy volume — which is exactly the condition
    /// a deep scan creates.
    /// </para>
    /// </summary>
    private static async Task<Result> PumpAsync(
        string progressPath,
        Action<ElevatedScanProgressReport> onProgress,
        CancellationToken ct)
    {
        var offset = 0L;
        var idle = TimeSpan.Zero;

        // The worker has to survive a UAC prompt the user may take a while to answer,
        // so silence is not treated as failure for a generous while.
        var patience = TimeSpan.FromMinutes(5);
        var poll = TimeSpan.FromMilliseconds(200);

        while (!ct.IsCancellationRequested)
        {
            var (lines, newOffset) = ReadFrom(progressPath, offset);
            offset = newOffset;

            if (lines.Count > 0)
                idle = TimeSpan.Zero;

            foreach (var line in lines)
            {
                var report = ElevatedScanProgressReport.TryParse(line);
                if (report is null)
                    continue;

                onProgress(report);

                if (report.IsComplete)
                    return new Result(true, report.Error is null, report.SessionId, report.Status, report.Error);
            }

            await Task.Delay(poll, ct).ConfigureAwait(false);
            idle += poll;

            if (idle > patience)
                return Result.Failed("The elevated scan stopped reporting.");
        }

        ct.ThrowIfCancellationRequested();
        return Result.Failed("Cancelled.");
    }

    private static (IReadOnlyList<string> Lines, long Offset) ReadFrom(string path, long offset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= offset)
                return ([], offset);

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            var lines = new List<string>();
            var consumed = offset;

            while (reader.ReadLine() is { } line)
            {
                // A line without a terminator is still being written. Stop before it
                // and leave the offset where it began so the next read gets it whole.
                if (reader.EndOfStream && !line.EndsWith('}'))
                    break;

                lines.Add(line);
                consumed += System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }

            return (lines, consumed);
        }
        catch (IOException)
        {
            return ([], offset);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover progress file in TEMP is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
