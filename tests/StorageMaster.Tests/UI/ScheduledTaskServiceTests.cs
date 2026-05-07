using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.Tests.UI;

/// <summary>
/// Unit tests for ScheduledTaskService.GetJobAsync and UpdateRunOutcomeAsync.
/// Tests target the pure data-management logic (settings load/save) and do not
/// invoke schtasks.exe. No WinUI shell required.
/// </summary>
public sealed class ScheduledTaskServiceTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static ScheduledTaskService BuildService(
        Mock<ISettingsRepository> repo,
        Mock<ILocalDiagnosticsService>? diag = null)
    {
        var d = diag ?? new Mock<ILocalDiagnosticsService>();
        d.Setup(x => x.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
         .Returns(Task.CompletedTask);

        return new ScheduledTaskService(
            repo.Object,
            d.Object,
            NullLogger<ScheduledTaskService>.Instance);
    }

    private static ScheduledJobDefinition MakeJob(string id, string name = "Test Job") =>
        new() { Id = id, Name = name, Kind = ScheduledJobKind.Scan };

    private static Mock<ISettingsRepository> RepoWith(params ScheduledJobDefinition[] jobs)
    {
        var settings = new AppSettings { ScheduledJobs = [.. jobs] };
        var repo = new Mock<ISettingsRepository>();
        repo.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);
        repo.Setup(r => r.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return repo;
    }

    // ── GetJobAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJobAsync_ExistingId_ReturnsJob()
    {
        var job = MakeJob("abc123", "My Scan");
        var repo = RepoWith(job);
        var svc = BuildService(repo);

        var result = await svc.GetJobAsync("abc123");

        result.Should().NotBeNull();
        result!.Id.Should().Be("abc123");
        result.Name.Should().Be("My Scan");
    }

    [Fact]
    public async Task GetJobAsync_MissingId_ReturnsNull()
    {
        var job = MakeJob("abc123");
        var repo = RepoWith(job);
        var svc = BuildService(repo);

        var result = await svc.GetJobAsync("doesnotexist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetJobAsync_EmptyJobList_ReturnsNull()
    {
        var repo = RepoWith();
        var svc = BuildService(repo);

        var result = await svc.GetJobAsync("anything");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetJobAsync_IsCaseInsensitive()
    {
        var job = MakeJob("AbCdEf");
        var repo = RepoWith(job);
        var svc = BuildService(repo);

        var result = await svc.GetJobAsync("abcdef");

        result.Should().NotBeNull();
    }

    // ── UpdateRunOutcomeAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateRunOutcomeAsync_ExistingId_PersistsStatusAndMessage()
    {
        var job = MakeJob("job1");
        AppSettings? saved = null;
        var repo = new Mock<ISettingsRepository>();
        repo.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { ScheduledJobs = [job] });
        repo.Setup(r => r.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var svc = BuildService(repo);
        await svc.UpdateRunOutcomeAsync("job1", "Success", "Scan completed successfully.");

        saved.Should().NotBeNull();
        var updatedJob = saved!.ScheduledJobs.FirstOrDefault(j => j.Id == "job1");
        updatedJob.Should().NotBeNull();
        updatedJob!.LastStatus.Should().Be("Success");
        updatedJob.LastMessage.Should().Be("Scan completed successfully.");
        updatedJob.LastRunUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateRunOutcomeAsync_MissingId_DoesNotSave()
    {
        var job = MakeJob("realJob");
        var repo = RepoWith(job);
        var svc = BuildService(repo);

        await svc.UpdateRunOutcomeAsync("nonexistent", "Failed", "error");

        repo.Verify(r => r.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRunOutcomeAsync_SetsLastRunUtcToApproximatelyNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var job = MakeJob("j99");
        AppSettings? saved = null;
        var repo = new Mock<ISettingsRepository>();
        repo.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { ScheduledJobs = [job] });
        repo.Setup(r => r.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var svc = BuildService(repo);
        await svc.UpdateRunOutcomeAsync("j99", "Done", "ok");

        var after = DateTime.UtcNow.AddSeconds(1);
        var updatedJob = saved!.ScheduledJobs.First(j => j.Id == "j99");
        updatedJob.LastRunUtc.Should().NotBeNull();
        updatedJob.LastRunUtc!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task UpdateRunOutcomeAsync_OtherJobsUnchanged()
    {
        var job1 = MakeJob("j1", "Job One");
        var job2 = MakeJob("j2", "Job Two");
        AppSettings? saved = null;
        var repo = new Mock<ISettingsRepository>();
        repo.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings { ScheduledJobs = [job1, job2] });
        repo.Setup(r => r.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppSettings, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        var svc = BuildService(repo);
        await svc.UpdateRunOutcomeAsync("j1", "Success", "done");

        var unchanged = saved!.ScheduledJobs.First(j => j.Id == "j2");
        unchanged.LastStatus.Should().BeNullOrEmpty();
        unchanged.LastMessage.Should().BeNullOrEmpty();
        unchanged.LastRunUtc.Should().BeNull();
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJobAsync_CancelledToken_ThrowsOrExitsEarly()
    {
        var repo = new Mock<ISettingsRepository>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        repo.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var svc = BuildService(repo);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.GetJobAsync("any", cts.Token));
    }
}
