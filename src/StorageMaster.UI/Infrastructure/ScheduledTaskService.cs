using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Safety;
using StorageMaster.Core.Scheduling;

namespace StorageMaster.UI.Infrastructure;

internal delegate Task<ScheduledTaskCommandResult> ScheduledTaskCommandRunner(
    IReadOnlyList<string> arguments,
    CancellationToken ct,
    int maxCapturedCharacters);

internal sealed record ScheduledTaskCommandResult(int ExitCode, string Output);

public sealed class ScheduledTaskService : IScheduledTaskService
{
    private readonly ISettingsRepository settingsRepository;
    private readonly ILocalDiagnosticsService diagnostics;
    private readonly ILogger<ScheduledTaskService> logger;
    private readonly ScheduledTaskCommandRunner commandRunner;

    public ScheduledTaskService(
        ISettingsRepository settingsRepository,
        ILocalDiagnosticsService diagnostics,
        ILogger<ScheduledTaskService> logger)
        : this(settingsRepository, diagnostics, logger, RunSchtasksProcessAsync)
    {
    }

    internal ScheduledTaskService(
        ISettingsRepository settingsRepository,
        ILocalDiagnosticsService diagnostics,
        ILogger<ScheduledTaskService> logger,
        ScheduledTaskCommandRunner commandRunner)
    {
        this.settingsRepository = settingsRepository;
        this.diagnostics = diagnostics;
        this.logger = logger;
        this.commandRunner = commandRunner;
    }

    public async Task<IReadOnlyList<ScheduledTaskInfo>> ListAsync(CancellationToken ct = default)
    {
        var settings = await settingsRepository.LoadAsync(ct);
        var list = new List<ScheduledTaskInfo>(settings.ScheduledJobs.Count);
        foreach (var job in settings.ScheduledJobs)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(await ReadTaskInfoAsync(job, ct));
        }

        return list
            .OrderBy(static item => item.Job.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ScheduledTaskInfo> UpsertAsync(ScheduledJobDefinition job, CancellationToken ct = default)
    {
        var normalized = Normalize(job);
        var settingsBefore = await settingsRepository.LoadAsync(ct);
        var previous = settingsBefore.ScheduledJobs.FirstOrDefault(existing =>
            string.Equals(existing.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
        var settingsSnapshot = SchedulerSettingsSnapshot.Capture(settingsBefore);
        var settingsMutationAttempted = false;
        ScheduledTaskInfo result;

        try
        {
            await ApplyTaskDefinitionAsync(normalized, previous, ct);
            settingsMutationAttempted = true;
            await settingsRepository.UpdateAsync(settings =>
            {
                var jobs = settings.ScheduledJobs
                    .Where(existing => !string.Equals(existing.Id, normalized.Id, StringComparison.OrdinalIgnoreCase))
                    .Append(normalized)
                    .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                settings.ScheduledJobs = jobs;
                settings.ScheduledTasksEnabled = jobs.Any(static item => item.Enabled);
            }, ct);

            result = await ReadTaskInfoAsync(normalized, ct);
        }
        catch (Exception operationException)
        {
            await RollBackSchedulerStateOrThrowAsync(
                settingsSnapshot,
                previous,
                normalized,
                settingsMutationAttempted,
                operationException);
            throw;
        }

        await RecordDiagnosticsBestEffortAsync($"upserted|{normalized.Id}|{normalized.Name}");
        return result;
    }

    public async Task DeleteAsync(string jobId, CancellationToken ct = default)
    {
        var settingsBefore = await settingsRepository.LoadAsync(ct);
        var job = settingsBefore.ScheduledJobs.FirstOrDefault(existing =>
            string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase));
        if (job is null)
            return;

        var settingsSnapshot = SchedulerSettingsSnapshot.Capture(settingsBefore);
        var settingsMutationAttempted = false;

        try
        {
            await DeleteKnownTasksAsync(job, ct);
            settingsMutationAttempted = true;
            await settingsRepository.UpdateAsync(settings =>
            {
                settings.ScheduledJobs = settings.ScheduledJobs
                    .Where(existing => !string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                settings.ScheduledTasksEnabled = settings.ScheduledJobs.Any(static item => item.Enabled);
            }, ct);
        }
        catch (Exception operationException)
        {
            await RollBackSchedulerStateOrThrowAsync(
                settingsSnapshot,
                job,
                job,
                settingsMutationAttempted,
                operationException);
            throw;
        }

        await RecordDiagnosticsBestEffortAsync($"deleted|{job.Id}|{job.Name}");
    }

    public async Task UpdateRunOutcomeAsync(string jobId, string status, string message, CancellationToken ct = default)
    {
        var settings = await settingsRepository.LoadAsync(ct);
        var job = settings.ScheduledJobs.FirstOrDefault(existing =>
            string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase));
        if (job is null)
            return;

        await settingsRepository.UpdateAsync(settings =>
        {
            settings.ScheduledJobs = settings.ScheduledJobs
                .Select(existing => string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase)
                    ? existing with
                    {
                        LastRunUtc = DateTime.UtcNow,
                        LastStatus = status,
                        LastMessage = message,
                    }
                    : existing)
                .ToList();
        }, ct);
        await RecordDiagnosticsBestEffortAsync($"run|{jobId}|{status}|{message}");
    }

    public async Task<ScheduledJobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        var settings = await settingsRepository.LoadAsync(ct);
        return settings.ScheduledJobs.FirstOrDefault(existing =>
            string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ScheduledTaskInfo> ReadTaskInfoAsync(ScheduledJobDefinition job, CancellationToken ct)
    {
        var taskName = GetTaskName(job);
        var lookup = await QueryTaskAsync(taskName, ct);
        if (!lookup.Exists)
        {
            var legacyName = GetLegacyTaskName(job);
            var legacyLookup = await QueryTaskAsync(legacyName, ct);
            if (legacyLookup.Exists)
            {
                taskName = legacyName;
                lookup = legacyLookup;
            }
        }

        var query = lookup.Query;

        return new ScheduledTaskInfo
        {
            Job = job,
            TaskName = taskName,
            NextRunTimeText = TryReadListField(query.Output, "Next Run Time") ?? TryReadListField(query.Output, "Nächste Laufzeit") ?? "Not scheduled",
            StatusText = TryReadListField(query.Output, "Status") ?? "Unknown",
        };
    }

    private async Task ApplyTaskDefinitionAsync(
        ScheduledJobDefinition job,
        ScheduledJobDefinition? previous,
        CancellationToken ct)
    {
        if (!job.Enabled)
        {
            await DeleteKnownTasksAsync(job, ct, previous);
            return;
        }

        var legacyTasks = GetLegacyTaskNames(job, previous);
        var legacyLookups = await PreflightTaskLookupsAsync(legacyTasks, ct);
        await CreateOrUpdateTaskAsync(job, ct);
        await DeletePreflightedTasksAsync(legacyLookups, ct);
    }

    private async Task CreateOrUpdateTaskAsync(ScheduledJobDefinition job, CancellationToken ct)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var trValue = QuoteWindowsArgument(exePath) +
            " --headless jobs run --id " + QuoteWindowsArgument(job.Id);
        var taskArgs = new List<string>
        {
            "/Create", "/F",
            "/SC", job.Frequency == ScheduledJobFrequency.Daily ? "DAILY" : "WEEKLY",
        };
        if (job.Frequency == ScheduledJobFrequency.Weekly)
        {
            taskArgs.Add("/D");
            taskArgs.Add(MapDay(job.WeeklyDay));
        }
        taskArgs.AddRange([
            "/TN", GetTaskName(job),
            "/TR", trValue,
            "/ST", job.StartTimeLocal,
            "/RL", "HIGHEST",
        ]);

        await RunSchtasksAsync(taskArgs, ct, tolerateFailure: false);
        logger.LogInformation("Scheduled task upserted: {TaskName}", GetTaskName(job));
    }

    private static ScheduledJobDefinition Normalize(ScheduledJobDefinition job)
    {
        var id = string.IsNullOrWhiteSpace(job.Id) ? Guid.NewGuid().ToString("N") : job.Id.Trim();
        if (id.Length > 64 || id.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException("Scheduled job ID contains unsupported characters.");
        }
        var name = string.IsNullOrWhiteSpace(job.Name)
            ? $"{job.Kind} {id[..Math.Min(8, id.Length)]}"
            : new string(job.Name.Trim().Where(static character => !char.IsControl(character)).Take(120).ToArray());
        return job with
        {
            Id = id,
            Name = name,
            StartTimeLocal = NormalizeTime(job.StartTimeLocal),
            TargetPath = job.TargetPath.Trim(),
            RulesCsv = job.RulesCsv.Trim(),
        };
    }

    private static string NormalizeTime(string value)
    {
        if (TimeOnly.TryParse(value, out var parsed))
            return parsed.ToString("HH:mm");

        return "09:00";
    }

    private async Task<ScheduledTaskCommandResult> RunSchtasksAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        bool tolerateFailure,
        int maxCapturedCharacters = 64 * 1024)
    {
        var result = await commandRunner(arguments, ct, maxCapturedCharacters);
        if (result.ExitCode != 0 && !tolerateFailure)
            throw new InvalidOperationException(result.Output);

        return result;
    }

    private static async Task<ScheduledTaskCommandResult> RunSchtasksProcessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        int maxCapturedCharacters)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var result = await ExternalProcessRunner.RunAsync(
            startInfo,
            ct,
            maxCapturedCharacters);
        var output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;

        return new ScheduledTaskCommandResult(result.ExitCode, output);
    }

    private static string GetTaskName(ScheduledJobDefinition job)
    {
        var normalizedId = job.Id.Trim().ToUpperInvariant();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedId)))[..32];
        return "StorageMaster Job " + digest;
    }

    private static string GetLegacyTaskName(ScheduledJobDefinition job)
    {
        var prefix = job.Kind is ScheduledJobKind.Scan or ScheduledJobKind.ScanAndReport
            ? "StorageMaster Scan "
            : "StorageMaster Cleanup ";
        return prefix + job.Name;
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && value.All(static character =>
                !char.IsWhiteSpace(character) && character is not '"' and not '\\'))
        {
            return value;
        }

        var quoted = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    private async Task<TaskLookupResult> QueryTaskAsync(
        string taskName,
        CancellationToken ct)
    {
        var query = await RunSchtasksAsync(
            ["/Query", "/TN", taskName, "/FO", "LIST", "/V"],
            ct,
            tolerateFailure: true);
        if (query.ExitCode == 0)
            return new TaskLookupResult(taskName, true, query);

        var inventory = await RunSchtasksAsync(
            ["/Query", "/FO", "CSV", "/NH"],
            ct,
            tolerateFailure: true,
            maxCapturedCharacters: 16 * 1024 * 1024);
        var status = ScheduledTaskLookupPolicy.Evaluate(
            query.ExitCode,
            inventory.ExitCode,
            inventory.Output,
            taskName);
        if (status == ScheduledTaskLookupStatus.QueryFailed)
        {
            throw new InvalidOperationException(
                $"Could not determine whether scheduled task '{taskName}' exists. " +
                $"Targeted query: {query.Output} Inventory exit code: {inventory.ExitCode}.");
        }

        return new TaskLookupResult(taskName, status == ScheduledTaskLookupStatus.Found, query);
    }

    private async Task DeleteKnownTasksAsync(
        ScheduledJobDefinition job,
        CancellationToken ct,
        ScheduledJobDefinition? previous = null)
    {
        var lookups = await PreflightTaskLookupsAsync(GetKnownTaskNames(job, previous), ct);
        await DeletePreflightedTasksAsync(lookups, ct);
    }

    private async Task<IReadOnlyList<TaskLookupResult>> PreflightTaskLookupsAsync(
        IEnumerable<string> taskNames,
        CancellationToken ct)
    {
        var lookups = new List<TaskLookupResult>();
        foreach (var taskName in taskNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            lookups.Add(await QueryTaskAsync(taskName, ct));
        }

        return lookups;
    }

    private async Task DeletePreflightedTasksAsync(
        IReadOnlyList<TaskLookupResult> lookups,
        CancellationToken ct)
    {
        foreach (var lookup in lookups)
        {
            if (!lookup.Exists)
                continue;

            await RunSchtasksAsync(
                ["/Delete", "/TN", lookup.TaskName, "/F"],
                ct,
                tolerateFailure: false);
        }
    }

    private static IReadOnlyList<string> GetKnownTaskNames(
        ScheduledJobDefinition job,
        ScheduledJobDefinition? previous = null)
    {
        var names = new List<string>
        {
            GetTaskName(job),
            GetLegacyTaskName(job),
        };
        if (previous is not null)
            names.Add(GetLegacyTaskName(previous));

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> GetLegacyTaskNames(
        ScheduledJobDefinition job,
        ScheduledJobDefinition? previous)
    {
        var names = new List<string> { GetLegacyTaskName(job) };
        if (previous is not null)
            names.Add(GetLegacyTaskName(previous));

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task RollBackSchedulerStateOrThrowAsync(
        SchedulerSettingsSnapshot settingsSnapshot,
        ScheduledJobDefinition? previous,
        ScheduledJobDefinition attempted,
        bool restoreSettings,
        Exception operationException)
    {
        var rollbackErrors = new List<Exception>();
        try
        {
            if (previous?.Enabled == true)
                await CreateOrUpdateTaskAsync(previous, CancellationToken.None);
            else
                await DeleteKnownTasksAsync(attempted, CancellationToken.None, previous);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not restore the prior scheduled task definition");
            rollbackErrors.Add(ex);
        }

        if (restoreSettings)
        {
            try
            {
                await settingsRepository.UpdateAsync(settings =>
                {
                    settings.ScheduledJobs = settingsSnapshot.Jobs.ToList();
                    settings.ScheduledTasksEnabled = settingsSnapshot.ScheduledTasksEnabled;
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not restore the prior scheduler settings");
                rollbackErrors.Add(ex);
            }
        }

        if (rollbackErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "The scheduler operation failed and its prior state could not be fully restored.",
                new AggregateException([operationException, .. rollbackErrors]));
        }
    }

    private static string MapDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "SUN",
    };

    private static string? TryReadListField(string text, string key)
    {
        var prefix = key + ":";
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line[prefix.Length..].Trim();
        }

        return null;
    }

    private async Task RecordDiagnosticsBestEffortAsync(string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await diagnostics.RecordAsync("scheduler", message, timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduler diagnostics write failed after primary state was settled");
        }
    }

    private sealed record SchedulerSettingsSnapshot(
        bool ScheduledTasksEnabled,
        IReadOnlyList<ScheduledJobDefinition> Jobs)
    {
        public static SchedulerSettingsSnapshot Capture(AppSettings settings) =>
            new(settings.ScheduledTasksEnabled, settings.ScheduledJobs.ToArray());
    }

    private sealed record TaskLookupResult(
        string TaskName,
        bool Exists,
        ScheduledTaskCommandResult Query);
}
