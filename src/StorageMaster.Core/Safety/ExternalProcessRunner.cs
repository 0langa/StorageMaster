using System.Diagnostics;
using System.Text;

namespace StorageMaster.Core.Safety;

/// <summary>
/// Runs a redirected child process without pipe deadlocks and terminates its
/// process tree when the operation is cancelled.
/// </summary>
public static class ExternalProcessRunner
{
    private const int DefaultCapturedCharacters = 64 * 1024;
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken = default,
        int maxCapturedCharacters = DefaultCapturedCharacters)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (maxCapturedCharacters < 0)
            throw new ArgumentOutOfRangeException(nameof(maxCapturedCharacters));

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {startInfo.FileName}.");

        var stdoutTask = startInfo.RedirectStandardOutput
            ? DrainAsync(process.StandardOutput, maxCapturedCharacters)
            : Task.FromResult(string.Empty);
        var stderrTask = startInfo.RedirectStandardError
            ? DrainAsync(process.StandardError, maxCapturedCharacters)
            : Task.FromResult(string.Empty);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(KillWaitTimeout)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                // Cancellation remains the caller-visible result. Disposing the
                // process below closes redirected streams if termination failed.
            }

            ObserveFault(stdoutTask);
            ObserveFault(stderrTask);
            throw;
        }

        var output = await stdoutTask.ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new ExternalProcessResult(process.ExitCode, output, error);
    }

    private static async Task<string> DrainAsync(StreamReader reader, int maxCapturedCharacters)
    {
        var captured = new StringBuilder(Math.Min(maxCapturedCharacters, 4096));
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            var remaining = maxCapturedCharacters - captured.Length;
            if (remaining > 0)
                captured.Append(buffer, 0, Math.Min(read, remaining));
        }

        return captured.ToString();
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Process already exited or could not be terminated. Bounded wait
            // and disposal still prevent cancellation from hanging forever.
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

public sealed record ExternalProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
