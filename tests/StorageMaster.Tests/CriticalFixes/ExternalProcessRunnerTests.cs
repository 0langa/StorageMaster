using System.Diagnostics;
using FluentAssertions;
using StorageMaster.Core.Safety;

namespace StorageMaster.Tests.CriticalFixes;

public sealed class ExternalProcessRunnerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"StorageMaster-process-{Guid.NewGuid():N}");

    public ExternalProcessRunnerTests() => Directory.CreateDirectory(_tempDirectory);

    [Fact]
    public async Task RunAsync_LargeRedirectedError_DoesNotDeadlockAndBoundsCapture()
    {
        var startInfo = CreatePowerShellStartInfo(
            "[Console]::Error.Write(('x' * 200000)); exit 17");

        var result = await ExternalProcessRunner.RunAsync(
            startInfo,
            maxCapturedCharacters: 4096);

        result.ExitCode.Should().Be(17);
        result.StandardError.Should().HaveLength(4096);
    }

    [Fact]
    public async Task RunAsync_Cancellation_KillsProcessBeforeDelayedMutation()
    {
        var markerPath = Path.Combine(_tempDirectory, "should-not-exist.txt");
        var startInfo = CreatePowerShellStartInfo(
            "$marker = $env:STORAGEMASTER_PROCESS_TEST_MARKER; Start-Sleep -Seconds 30; " +
            "[IO.File]::WriteAllText($marker, 'unexpected')");
        startInfo.Environment["STORAGEMASTER_PROCESS_TEST_MARKER"] = markerPath;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        Func<Task> act = () => ExternalProcessRunner.RunAsync(startInfo, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
        await Task.Delay(750);
        File.Exists(markerPath).Should().BeFalse();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ProcessStartInfo CreatePowerShellStartInfo(
        string command,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}
