namespace StorageMaster.Core.Scheduling;

public sealed record ScheduledJobRunCoordinationResult(
    string OutcomeStatus,
    string OutcomeMessage,
    Exception? WorkException,
    Exception? OutcomeWriteException)
{
    public bool WorkSucceeded => WorkException is null;
}

/// <summary>
/// Keeps scheduled work outcome separate from best-effort status persistence.
/// A bookkeeping failure never replaces or relabels the work result.
/// </summary>
public static class ScheduledJobRunCoordinator
{
    public static async Task<ScheduledJobRunCoordinationResult> RunAsync(
        Func<CancellationToken, Task> executeWork,
        Func<string, string, CancellationToken, Task> writeOutcome,
        TimeSpan outcomeWriteTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executeWork);
        ArgumentNullException.ThrowIfNull(writeOutcome);
        if (outcomeWriteTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(outcomeWriteTimeout));

        Exception? workException = null;
        try
        {
            await executeWork(cancellationToken);
        }
        catch (Exception ex)
        {
            workException = ex;
        }

        var status = workException switch
        {
            null => "Success",
            OperationCanceledException => "Cancelled",
            _ => "Failed",
        };
        var message = workException switch
        {
            null => "Completed successfully.",
            OperationCanceledException => "Operation cancelled.",
            _ when string.IsNullOrWhiteSpace(workException.Message) => "Scheduled job failed without error details.",
            _ => workException.Message,
        };

        Exception? outcomeWriteException = null;
        try
        {
            using var timeout = new CancellationTokenSource(outcomeWriteTimeout);
            await writeOutcome(status, message, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            outcomeWriteException = ex;
        }

        return new ScheduledJobRunCoordinationResult(
            status,
            message,
            workException,
            outcomeWriteException);
    }
}
