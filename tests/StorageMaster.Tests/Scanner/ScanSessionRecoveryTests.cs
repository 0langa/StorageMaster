using FluentAssertions;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Tests.Scanner;

public sealed class ScanSessionRecoveryTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SessionWithNoRecordedOwnerIsAbandoned()
    {
        var session = Running(1, ownerId: null, ownerStart: null);

        var abandoned = ScanSessionRecovery.FindAbandoned([session], [], currentProcessId: 999);

        abandoned.Should().ContainSingle().Which.Id.Should().Be(1,
            "rows written before ownership tracking cannot be claimed by any live process");
    }

    [Fact]
    public void SessionOwnedByALiveProcessIsLeftAlone()
    {
        var start = Now.AddMinutes(-5);
        var session = Running(2, ownerId: 4242, ownerStart: start);

        var abandoned = ScanSessionRecovery.FindAbandoned(
            [session],
            [new ScanSessionRecovery.LiveProcess(4242, start)],
            currentProcessId: 999);

        abandoned.Should().BeEmpty(
            "a headless scan may legitimately be running while another instance starts");
    }

    [Fact]
    public void SessionOwnedByADeadProcessIsAbandoned()
    {
        var session = Running(3, ownerId: 4242, ownerStart: Now.AddMinutes(-5));

        var abandoned = ScanSessionRecovery.FindAbandoned([session], [], currentProcessId: 999);

        abandoned.Should().ContainSingle().Which.Id.Should().Be(3);
    }

    [Fact]
    public void RecycledProcessIdDoesNotKeepADeadSessionAlive()
    {
        // Same id, but the live process started long after the scan did.
        var session = Running(4, ownerId: 4242, ownerStart: Now.AddHours(-3));

        var abandoned = ScanSessionRecovery.FindAbandoned(
            [session],
            [new ScanSessionRecovery.LiveProcess(4242, Now.AddMinutes(-1))],
            currentProcessId: 999);

        abandoned.Should().ContainSingle().Which.Id.Should().Be(4,
            "process ids are recycled, so the start time must be matched too");
    }

    [Fact]
    public void SmallClockDriftDoesNotCondemnALiveScan()
    {
        var recorded = Now.AddMinutes(-5);
        var session = Running(5, ownerId: 4242, ownerStart: recorded);

        var abandoned = ScanSessionRecovery.FindAbandoned(
            [session],
            [new ScanSessionRecovery.LiveProcess(4242, recorded.AddMilliseconds(900))],
            currentProcessId: 999);

        abandoned.Should().BeEmpty(
            "recorded and observed start times come from different clocks and round differently");
    }

    [Fact]
    public void OwnSessionsAreNeverCondemned()
    {
        var session = Running(6, ownerId: 777, ownerStart: Now.AddMinutes(-1));

        var abandoned = ScanSessionRecovery.FindAbandoned([session], [], currentProcessId: 777);

        abandoned.Should().BeEmpty("the calling process may be about to resume its own scan");
    }

    [Theory]
    [InlineData(ScanStatus.Completed)]
    [InlineData(ScanStatus.Cancelled)]
    [InlineData(ScanStatus.Failed)]
    [InlineData(ScanStatus.Interrupted)]
    public void TerminalSessionsAreIgnored(ScanStatus status)
    {
        var session = Running(7, ownerId: null, ownerStart: null) with { Status = status };

        var abandoned = ScanSessionRecovery.FindAbandoned([session], [], currentProcessId: 999);

        abandoned.Should().BeEmpty("only Running sessions can be abandoned");
    }

    [Fact]
    public void InterruptedSessionKeepsItsPartialTotals()
    {
        var session = Running(8, ownerId: null, ownerStart: null) with
        {
            TotalFiles = 1_203_735,
            TotalFolders = 213_256,
            TotalSizeBytes = 120_000_000_000,
        };

        var recovered = ScanSessionRecovery.ToInterrupted(session, Now);

        recovered.Status.Should().Be(ScanStatus.Interrupted);
        recovered.CompletedUtc.Should().Be(Now);
        recovered.TotalFiles.Should().Be(1_203_735, "partial results are still useful data");
        recovered.TotalFolders.Should().Be(213_256);
        recovered.TotalSizeBytes.Should().Be(120_000_000_000);
        recovered.ErrorMessage.Should().Contain("Interrupted");
    }

    [Fact]
    public void ExistingErrorMessageIsPreserved()
    {
        var session = Running(9, ownerId: null, ownerStart: null) with
        {
            ErrorMessage = "Disk detached mid-scan",
        };

        ScanSessionRecovery.ToInterrupted(session, Now).ErrorMessage
            .Should().Be("Disk detached mid-scan",
                "a specific recorded cause is more useful than the generic one");
    }

    private static ScanSession Running(long id, int? ownerId, DateTime? ownerStart) => new()
    {
        Id = id,
        RootPath = @"C:\",
        StartedUtc = Now.AddMinutes(-10),
        Status = ScanStatus.Running,
        OwnerProcessId = ownerId,
        OwnerProcessStartedUtc = ownerStart,
    };
}
