using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// Reconciles scan sessions left <see cref="ScanStatus.Running"/> by a process that
/// never finished — a crash, a kill, or a power loss.
/// <para>
/// Runs once at startup. Liveness is decided by
/// <see cref="ScanSessionRecovery"/>; this type only supplies the observed process
/// list and writes the results, so the interesting rules stay unit testable.
/// </para>
/// </summary>
public sealed class ScanSessionRecoveryService(
    IScanRepository repository,
    ILogger<ScanSessionRecoveryService> logger)
{
    /// <summary>
    /// How many recent sessions to inspect. Abandoned sessions are always recent
    /// relative to the crash that produced them, and this keeps startup bounded.
    /// </summary>
    private const int InspectionWindow = 200;

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        try
        {
            var sessions = await repository.GetRecentSessionsAsync(InspectionWindow, ct)
                .ConfigureAwait(false);

            var abandoned = ScanSessionRecovery.FindAbandoned(
                sessions,
                GetLiveStorageMasterProcesses(),
                Environment.ProcessId);

            if (abandoned.Count == 0)
                return 0;

            var nowUtc = DateTime.UtcNow;
            foreach (var session in abandoned)
            {
                ct.ThrowIfCancellationRequested();
                await repository
                    .UpdateSessionAsync(ScanSessionRecovery.ToInterrupted(session, nowUtc), ct)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Scan session {SessionId} at {Root} was left running by a process that exited; marked interrupted.",
                    session.Id, session.RootPath);
            }

            return abandoned.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Startup must never fail because history could not be tidied.
            logger.LogWarning(ex, "Could not reconcile abandoned scan sessions.");
            return 0;
        }
    }

    /// <summary>
    /// Only StorageMaster processes are considered, so an unrelated program that
    /// happens to reuse a recorded process id cannot keep a dead session pinned.
    /// </summary>
    private static IReadOnlyList<ScanSessionRecovery.LiveProcess> GetLiveStorageMasterProcesses()
    {
        var current = Process.GetCurrentProcess();
        var live = new List<ScanSessionRecovery.LiveProcess>();

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(current.ProcessName);
        }
        catch (InvalidOperationException)
        {
            return live;
        }

        foreach (var process in candidates)
        {
            try
            {
                live.Add(new ScanSessionRecovery.LiveProcess(
                    process.Id,
                    process.StartTime.ToUniversalTime()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process exited between enumeration and inspection, or its start
                // time is not readable. Treating it as not-live is the safe default:
                // the worst case is that a genuinely live scan is marked interrupted,
                // which loses no data because partial totals are preserved.
            }
            finally
            {
                process.Dispose();
            }
        }

        return live;
    }
}
