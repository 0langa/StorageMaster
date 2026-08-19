using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scanner;

/// <summary>
/// Decides which <see cref="ScanStatus.Running"/> sessions were abandoned.
/// <para>
/// Nothing used to reconcile these, so a crash, a kill or a power loss left a
/// session Running forever. The rows then looked identical to a scan in progress,
/// and their file and folder data was never reclaimable.
/// </para>
/// <para>
/// The naive fix — mark everything Running as dead at startup — is wrong, because
/// a headless CLI scan can legitimately be running while the UI starts. This type
/// therefore only condemns a session when no live process claims it.
/// </para>
/// <para>
/// Pure decision logic with no I/O, so the process-liveness rules are unit
/// testable without spawning anything.
/// </para>
/// </summary>
public static class ScanSessionRecovery
{
    /// <summary>
    /// Describes a process that is currently alive on the machine.
    /// </summary>
    /// <param name="ProcessId">The operating-system process id.</param>
    /// <param name="StartedUtc">
    /// When that process started. Required because process ids are recycled: without
    /// it, an unrelated new process reusing a dead scanner's id would keep the
    /// abandoned session looking alive indefinitely.
    /// </param>
    public readonly record struct LiveProcess(int ProcessId, DateTime StartedUtc);

    /// <summary>
    /// Tolerance when comparing recorded and observed process start times. The two
    /// values come from different clocks and round differently, so an exact match
    /// would spuriously condemn a live scan.
    /// </summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns the sessions that should be moved out of <see cref="ScanStatus.Running"/>.
    /// </summary>
    /// <param name="sessions">Candidate sessions; non-running ones are ignored.</param>
    /// <param name="liveProcesses">
    /// Processes alive right now that could legitimately own a scan. Callers should
    /// pass only StorageMaster processes, so an unrelated program cannot keep a
    /// session pinned.
    /// </param>
    /// <param name="currentProcessId">
    /// The calling process. Its own sessions are never condemned — it may be about
    /// to start or resume one.
    /// </param>
    public static IReadOnlyList<ScanSession> FindAbandoned(
        IEnumerable<ScanSession> sessions,
        IEnumerable<LiveProcess> liveProcesses,
        int currentProcessId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(liveProcesses);

        var live = liveProcesses.ToDictionary(p => p.ProcessId, p => p.StartedUtc);
        var abandoned = new List<ScanSession>();

        foreach (var session in sessions)
        {
            if (session.Status != ScanStatus.Running)
                continue;

            if (session.OwnerProcessId is not { } ownerId)
            {
                // Written before ownership tracking existed. No process claims it,
                // so it cannot be in progress.
                abandoned.Add(session);
                continue;
            }

            if (ownerId == currentProcessId)
                continue;

            if (!live.TryGetValue(ownerId, out var actualStart))
            {
                abandoned.Add(session);
                continue;
            }

            if (session.OwnerProcessStartedUtc is not { } recordedStart)
            {
                // A recorded id with no recorded start cannot be matched against a
                // recycled id, so trust the live process and leave the session alone.
                continue;
            }

            var drift = (actualStart - recordedStart).Duration();
            if (drift > StartTimeTolerance)
            {
                // Same id, different process: the id was recycled and the original
                // owner is gone.
                abandoned.Add(session);
            }
        }

        return abandoned;
    }

    /// <summary>
    /// Produces the terminal form of an abandoned session, preserving whatever
    /// partial totals were already persisted so the user keeps the data the scan
    /// managed to write.
    /// </summary>
    public static ScanSession ToInterrupted(ScanSession session, DateTime nowUtc) => session with
    {
        Status = ScanStatus.Interrupted,
        CompletedUtc = nowUtc,
        ErrorMessage = session.ErrorMessage
            ?? "Interrupted: the application closed before this scan finished. "
             + "Partial results were kept.",
    };
}
