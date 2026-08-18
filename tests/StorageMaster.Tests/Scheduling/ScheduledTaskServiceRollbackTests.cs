using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.Tests.Scheduling;

public sealed class ScheduledTaskServiceRollbackTests
{
    [Fact]
    public async Task DeleteAsync_WhenFirstDeleteWouldSucceedButSecondIdentityQueryFails_PreflightsBeforeDeleting()
    {
        var prior = CreateJob(name: "Nightly scan", startTime: "06:30");
        var repository = new InMemorySettingsRepository(new AppSettings
        {
            ScheduledJobs = [prior],
            ScheduledTasksEnabled = true,
        });
        var targetedQueries = 0;
        var runner = new ScriptedRunner(arguments =>
        {
            if (IsTargetedQuery(arguments))
            {
                targetedQueries++;
                return targetedQueries == 1
                    ? Success("Status: Ready")
                    : Failure("targeted query failed");
            }

            if (IsInventoryQuery(arguments))
                return Failure("inventory query failed");

            // This succeeds if the old query-delete-query implementation reaches it.
            if (IsCommand(arguments, "/Delete") || IsCommand(arguments, "/Create"))
                return Success();

            throw new InvalidOperationException($"Unexpected command: {string.Join(' ', arguments)}");
        });
        var service = CreateService(repository, runner);

        var act = () => service.DeleteAsync(prior.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Could not determine whether scheduled task*");
        runner.Calls.Should().NotContain(call => IsCommand(call, "/Delete"));
        runner.Calls.Count(call => IsCommand(call, "/Create")).Should().Be(1,
            "the prior enabled definition is restored even when task discovery fails");
        repository.UpdateCalls.Should().Be(0);
        repository.Current.ScheduledTasksEnabled.Should().BeTrue();
        repository.Current.ScheduledJobs.Should().ContainSingle().Which.Should().Be(prior);
    }

    [Fact]
    public async Task UpsertAsync_WhenEnabledUpdateLegacyCleanupFails_RestoresPriorDefinitionAndSettings()
    {
        var prior = CreateJob(name: "Old nightly scan", startTime: "06:30");
        var attempted = prior with { Name = "New nightly scan", StartTimeLocal = "22:15" };
        var repository = new InMemorySettingsRepository(new AppSettings
        {
            ScheduledJobs = [prior],
            ScheduledTasksEnabled = true,
        });
        var deleteCalls = 0;
        var runner = new ScriptedRunner(arguments =>
        {
            if (IsTargetedQuery(arguments) || IsCommand(arguments, "/Create"))
                return Success("Status: Ready");

            if (IsCommand(arguments, "/Delete"))
            {
                deleteCalls++;
                return deleteCalls == 1
                    ? Success()
                    : Failure("legacy cleanup failed");
            }

            throw new InvalidOperationException($"Unexpected command: {string.Join(' ', arguments)}");
        });
        var service = CreateService(repository, runner);

        var act = () => service.UpsertAsync(attempted);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("legacy cleanup failed");
        var createCalls = runner.Calls.Where(call => IsCommand(call, "/Create")).ToList();
        createCalls.Should().HaveCount(2);
        ReadArgument(createCalls[0], "/ST").Should().Be("22:15");
        ReadArgument(createCalls[1], "/ST").Should().Be("06:30",
            "rollback must overwrite the updated task with its prior definition");
        repository.UpdateCalls.Should().Be(0);
        repository.Current.ScheduledTasksEnabled.Should().BeTrue();
        repository.Current.ScheduledJobs.Should().ContainSingle().Which.Should().Be(prior);
    }

    [Fact]
    public async Task UpsertAsync_WhenSettingsWriteFailsAfterMutation_RestoresTaskAndSettingsSnapshots()
    {
        var prior = CreateJob(name: "Nightly scan", startTime: "06:30");
        var attempted = prior with { StartTimeLocal = "22:15" };
        var repository = new InMemorySettingsRepository(new AppSettings
        {
            ScheduledJobs = [prior],
            ScheduledTasksEnabled = true,
        })
        {
            FailAfterMutationOnUpdateCall = 1,
        };
        var runner = new ScriptedRunner(arguments =>
        {
            if (IsTargetedQuery(arguments))
                return Failure("not found");
            if (IsInventoryQuery(arguments) || IsCommand(arguments, "/Create"))
                return Success();

            throw new InvalidOperationException($"Unexpected command: {string.Join(' ', arguments)}");
        });
        var service = CreateService(repository, runner);

        var act = () => service.UpsertAsync(attempted);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("settings write failed after mutation");
        repository.UpdateCalls.Should().Be(2,
            "the second settings mutation restores the captured scheduler state");
        repository.Current.ScheduledTasksEnabled.Should().BeTrue();
        repository.Current.ScheduledJobs.Should().ContainSingle().Which.Should().Be(prior);
        var createCalls = runner.Calls.Where(call => IsCommand(call, "/Create")).ToList();
        createCalls.Should().HaveCount(2);
        ReadArgument(createCalls[0], "/ST").Should().Be("22:15");
        ReadArgument(createCalls[1], "/ST").Should().Be("06:30");
    }

    [Fact]
    public async Task UpsertAsync_WhenDiagnosticsWriteFails_ReturnsCommittedResult()
    {
        var job = CreateJob(name: "Nightly scan", startTime: "06:30");
        var repository = new InMemorySettingsRepository(new AppSettings());
        var targetedQueries = 0;
        var runner = new ScriptedRunner(arguments =>
        {
            if (IsTargetedQuery(arguments))
            {
                targetedQueries++;
                return targetedQueries == 1
                    ? Failure("legacy task not found")
                    : Success("Status: Ready");
            }

            if (IsInventoryQuery(arguments))
                return Success(string.Empty);
            if (IsCommand(arguments, "/Create"))
                return Success("Status: Ready");

            throw new InvalidOperationException($"Unexpected command: {string.Join(' ', arguments)}");
        });
        var service = new ScheduledTaskService(
            repository,
            new ThrowingDiagnosticsService(),
            NullLogger<ScheduledTaskService>.Instance,
            runner.RunAsync);

        var result = await service.UpsertAsync(job);

        result.Job.Id.Should().Be(job.Id);
        repository.Current.ScheduledJobs.Should().ContainSingle().Which.Id.Should().Be(job.Id);
        runner.Calls.Should().ContainSingle(call => IsCommand(call, "/Create"));
    }

    private static ScheduledTaskService CreateService(
        ISettingsRepository repository,
        ScriptedRunner runner) =>
        new(
            repository,
            new NullDiagnosticsService(),
            NullLogger<ScheduledTaskService>.Instance,
            runner.RunAsync);

    private static ScheduledJobDefinition CreateJob(string name, string startTime) => new()
    {
        Id = "nightly-scan",
        Name = name,
        Kind = ScheduledJobKind.Scan,
        Frequency = ScheduledJobFrequency.Daily,
        StartTimeLocal = startTime,
        TargetPath = @"C:\Data",
        Enabled = true,
    };

    private static bool IsTargetedQuery(IReadOnlyList<string> arguments) =>
        IsCommand(arguments, "/Query") && arguments.Contains("/TN", StringComparer.OrdinalIgnoreCase);

    private static bool IsInventoryQuery(IReadOnlyList<string> arguments) =>
        IsCommand(arguments, "/Query") && !arguments.Contains("/TN", StringComparer.OrdinalIgnoreCase);

    private static bool IsCommand(IReadOnlyList<string> arguments, string command) =>
        arguments.Count > 0 && string.Equals(arguments[0], command, StringComparison.OrdinalIgnoreCase);

    private static string ReadArgument(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        }

        throw new InvalidOperationException($"Argument '{name}' was not present.");
    }

    private static ScheduledTaskCommandResult Success(string output = "") => new(0, output);
    private static ScheduledTaskCommandResult Failure(string output) => new(1, output);

    private sealed class ScriptedRunner(
        Func<IReadOnlyList<string>, ScheduledTaskCommandResult> handler)
    {
        public IList<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ScheduledTaskCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken ct,
            int maxCapturedCharacters)
        {
            ct.ThrowIfCancellationRequested();
            var captured = arguments.ToArray();
            Calls.Add(captured);
            return Task.FromResult(handler(captured));
        }
    }

    private sealed class InMemorySettingsRepository(AppSettings current) : ISettingsRepository
    {
        public AppSettings Current { get; private set; } = current;
        public int UpdateCalls { get; private set; }
        public int? FailAfterMutationOnUpdateCall { get; init; }

        public Task<AppSettings> LoadAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Current = settings;
            return Task.CompletedTask;
        }

        public Task<AppSettings> UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            UpdateCalls++;
            mutate(Current);
            if (UpdateCalls == FailAfterMutationOnUpdateCall)
                throw new InvalidOperationException("settings write failed after mutation");

            return Task.FromResult(Current);
        }
    }

    private sealed class NullDiagnosticsService : ILocalDiagnosticsService
    {
        public Task RecordAsync(string category, string message, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string> ExportBundleAsync(CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class ThrowingDiagnosticsService : ILocalDiagnosticsService
    {
        public Task RecordAsync(string category, string message, CancellationToken ct = default) =>
            Task.FromException(new IOException("diagnostics unavailable"));

        public Task<string> ExportBundleAsync(CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }
}
