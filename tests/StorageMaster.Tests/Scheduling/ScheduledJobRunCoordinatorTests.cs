using FluentAssertions;
using StorageMaster.Core.Scheduling;

namespace StorageMaster.Tests.Scheduling;

public sealed class ScheduledJobRunCoordinatorTests
{
    [Fact]
    public async Task SuccessfulWork_OutcomeWriteFailure_DoesNotRelabelWork()
    {
        var writeError = new IOException("settings unavailable");

        var result = await ScheduledJobRunCoordinator.RunAsync(
            _ => Task.CompletedTask,
            (_, _, _) => Task.FromException(writeError),
            TimeSpan.FromSeconds(1));

        result.WorkSucceeded.Should().BeTrue();
        result.WorkException.Should().BeNull();
        result.OutcomeStatus.Should().Be("Success");
        result.OutcomeWriteException.Should().BeSameAs(writeError);
    }

    [Fact]
    public async Task FailedWork_OutcomeWriteFailure_PreservesBothExceptions()
    {
        var workError = new InvalidOperationException("original work failure");
        var writeError = new IOException("outcome write failure");

        var result = await ScheduledJobRunCoordinator.RunAsync(
            _ => Task.FromException(workError),
            (_, _, _) => Task.FromException(writeError),
            TimeSpan.FromSeconds(1));

        result.WorkException.Should().BeSameAs(workError);
        result.OutcomeStatus.Should().Be("Failed");
        result.OutcomeMessage.Should().Be(workError.Message);
        result.OutcomeWriteException.Should().BeSameAs(writeError);
    }

    [Fact]
    public async Task CancelledWork_UsesFreshTokenToRecordCancelledOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var writerTokenWasCancelled = true;
        string? recordedStatus = null;

        var result = await ScheduledJobRunCoordinator.RunAsync(
            token => Task.FromCanceled(token),
            (status, _, token) =>
            {
                recordedStatus = status;
                writerTokenWasCancelled = token.IsCancellationRequested;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        result.WorkException.Should().BeOfType<TaskCanceledException>();
        result.OutcomeStatus.Should().Be("Cancelled");
        recordedStatus.Should().Be("Cancelled");
        writerTokenWasCancelled.Should().BeFalse(
            "outcome persistence must not reuse the cancelled work token");
    }

    [Fact]
    public async Task HungOutcomeWriter_IsBoundedAndDoesNotChangeSuccessfulWork()
    {
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await ScheduledJobRunCoordinator.RunAsync(
            _ => Task.CompletedTask,
            (_, _, _) => neverCompletes.Task,
            TimeSpan.FromMilliseconds(50));

        result.WorkSucceeded.Should().BeTrue();
        result.OutcomeStatus.Should().Be("Success");
        result.OutcomeWriteException.Should().BeAssignableTo<OperationCanceledException>();
    }
}
