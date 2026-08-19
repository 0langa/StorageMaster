using StorageMaster.Core.Models;

namespace StorageMaster.UI.Infrastructure;

/// <summary>
/// Writes the progress channel an elevated scan worker reports through.
/// <para>
/// Created with a null path when the run is an ordinary CLI invocation, in which
/// case every method is a no-op and the scanner's progress sink stays empty. Only
/// a worker started by the UI passes <c>--progress</c>.
/// </para>
/// </summary>
public sealed class ElevatedScanProgressWriter : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    /// <summary>
    /// Progress is throttled to one line per 250 ms of wall clock. The scanner
    /// reports far more often than a person can read, and an unthrottled writer
    /// turns a million-file scan into a million disk writes on the very drive being
    /// measured.
    /// </summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);

    private DateTime _lastWriteUtc = DateTime.MinValue;

    private ElevatedScanProgressWriter(StreamWriter? writer) => _writer = writer;

    public static ElevatedScanProgressWriter Create(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new ElevatedScanProgressWriter(null);

        try
        {
            // Append rather than truncate: the UI creates the file before starting
            // the worker so that it owns it and can read it, and opening for append
            // keeps that ownership.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new ElevatedScanProgressWriter(new StreamWriter(stream) { AutoFlush = true });
        }
        catch (Exception)
        {
            // A scan that cannot report progress is still a scan worth running; the
            // UI falls back to waiting for the process to exit.
            return new ElevatedScanProgressWriter(null);
        }
    }

    public IProgress<ScanProgress> Sink => new Progress<ScanProgress>(Report);

    private void Report(ScanProgress progress)
    {
        if (_writer is null)
            return;

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastWriteUtc < MinimumInterval)
                return;

            _lastWriteUtc = now;

            Write(new ElevatedScanProgressReport
            {
                FilesScanned = progress.FilesScanned,
                FoldersScanned = progress.FoldersScanned,
                BytesScanned = progress.BytesScanned,
                ErrorCount = progress.ErrorCount,
                CurrentPath = progress.CurrentPath,
                IsComplete = false,
            });
        }
    }

    /// <summary>
    /// Writes the terminal line. The UI waits for this rather than for the process
    /// to exit, so it can show the outcome instead of only that the work stopped.
    /// </summary>
    public void WriteCompletion(ScanSession session)
    {
        if (_writer is null)
            return;

        lock (_gate)
        {
            Write(new ElevatedScanProgressReport
            {
                FilesScanned = session.TotalFiles,
                FoldersScanned = session.TotalFolders,
                BytesScanned = session.TotalSizeBytes,
                ErrorCount = (int)Math.Min(session.AccessDeniedCount, int.MaxValue),
                IsComplete = true,
                SessionId = session.Id,
                Status = session.Status.ToString(),
            });
        }
    }

    /// <summary>Reports a worker that failed before producing a session.</summary>
    public void WriteFailure(string error)
    {
        if (_writer is null)
            return;

        lock (_gate)
        {
            Write(new ElevatedScanProgressReport
            {
                IsComplete = true,
                Status = nameof(ScanStatus.Failed),
                Error = error,
            });
        }
    }

    private void Write(ElevatedScanProgressReport report)
    {
        try
        {
            _writer!.WriteLine(report.ToJsonLine());
        }
        catch (IOException)
        {
            // The UI may have gone away. Losing the channel must not stop the scan.
        }
    }

    public void Dispose() => _writer?.Dispose();
}
