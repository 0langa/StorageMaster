using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.UI.Infrastructure;

public sealed class ScheduledTaskService(
    ISettingsRepository settingsRepository,
    ILocalDiagnosticsService diagnostics,
    ILogger<ScheduledTaskService> logger) : IScheduledTaskService
{
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
        await CreateOrUpdateTaskAsync(normalized, ct);

        var settings = await settingsRepository.LoadAsync(ct);
        var jobs = settings.ScheduledJobs
            .Where(existing => !string.Equals(existing.Id, normalized.Id, StringComparison.OrdinalIgnoreCase))
            .Append(normalized)
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.ScheduledJobs = jobs;
        settings.ScheduledTasksEnabled = jobs.Any(static item => item.Enabled);
        await settingsRepository.SaveAsync(settings, ct);

        await diagnostics.RecordAsync("scheduler", $"upserted|{normalized.Id}|{normalized.Name}", ct);
        return await ReadTaskInfoAsync(normalized, ct);
    }

    public async Task DeleteAsync(string jobId, CancellationToken ct = default)
    {
        var settings = await settingsRepository.LoadAsync(ct);
        var job = settings.ScheduledJobs.FirstOrDefault(existing =>
            string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase));
        if (job is null)
            return;

        await RunSchtasksAsync($"/Delete /TN \"{GetTaskName(job)}\" /F", ct, tolerateFailure: true);

        settings.ScheduledJobs = settings.ScheduledJobs
            .Where(existing => !string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        settings.ScheduledTasksEnabled = settings.ScheduledJobs.Any(static item => item.Enabled);
        await settingsRepository.SaveAsync(settings, ct);
        await diagnostics.RecordAsync("scheduler", $"deleted|{job.Id}|{job.Name}", ct);
    }

    public async Task UpdateRunOutcomeAsync(string jobId, string status, string message, CancellationToken ct = default)
    {
        var settings = await settingsRepository.LoadAsync(ct);
        var job = settings.ScheduledJobs.FirstOrDefault(existing =>
            string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase));
        if (job is null)
            return;

        var updated = settings.ScheduledJobs
            .Select(existing => string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase)
                ? existing with
                {
                    LastRunUtc = DateTime.UtcNow,
                    LastStatus = status,
                    LastMessage = message,
                }
                : existing)
            .ToList();

        settings.ScheduledJobs = updated;
        await settingsRepository.SaveAsync(settings, ct);
        await diagnostics.RecordAsync("scheduler", $"run|{jobId}|{status}|{message}", ct);
    }

    public async Task<ScheduledJobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        var settings = await settingsRepository.LoadAsync(ct);
        return settings.ScheduledJobs.FirstOrDefault(existing =>
            string.Equals(existing.Id, jobId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ScheduledTaskInfo> ReadTaskInfoAsync(ScheduledJobDefinition job, CancellationToken ct)
    {
        var output = await RunSchtasksAsync($"/Query /TN \"{GetTaskName(job)}\" /FO LIST /V", ct, tolerateFailure: true);
        return new ScheduledTaskInfo
        {
            Job = job,
            TaskName = GetTaskName(job),
            NextRunTimeText = TryReadListField(output, "Next Run Time") ?? TryReadListField(output, "Nächste Laufzeit") ?? "Not scheduled",
            StatusText = TryReadListField(output, "Status") ?? "Unknown",
        };
    }

    private async Task CreateOrUpdateTaskAsync(ScheduledJobDefinition job, CancellationToken ct)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current executable path is unavailable.");
        var schedule = job.Frequency == ScheduledJobFrequency.Daily
            ? "/SC DAILY"
            : $"/SC WEEKLY /D {MapDay(job.WeeklyDay)}";
        // schtasks /TR expects a single quoted value: "program args"
        // If exePath has spaces, inner-escape with \" so CommandLineToArgvW parses it correctly.
        var trProgram = exePath.Contains(' ') ? "\\\"" + exePath + "\\\"" : exePath;
        var trValue = trProgram + " --headless jobs run --id " + job.Id;
        var taskArgs = new StringBuilder()
            .Append("/Create /F ")
            .Append(schedule)
            .Append(' ')
            .Append("/TN ").Append(Quote(GetTaskName(job))).Append(' ')
            .Append("/TR ").Append(Quote(trValue)).Append(' ')
            .Append("/ST ").Append(job.StartTimeLocal).Append(' ')
            .Append("/RL HIGHEST")
            .ToString();

        if (!job.Enabled)
        {
            await RunSchtasksAsync($"/Delete /TN \"{GetTaskName(job)}\" /F", ct, tolerateFailure: true);
            return;
        }

        await RunSchtasksAsync(taskArgs, ct, tolerateFailure: false);
        logger.LogInformation("Scheduled task upserted: {TaskName}", GetTaskName(job));
    }

    private static ScheduledJobDefinition Normalize(ScheduledJobDefinition job)
    {
        var id = string.IsNullOrWhiteSpace(job.Id) ? Guid.NewGuid().ToString("N") : job.Id.Trim();
        var name = string.IsNullOrWhiteSpace(job.Name)
            ? $"{job.Kind} {id[..8]}"
            : job.Name.Trim();
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

    private async Task<string> RunSchtasksAsync(string arguments, CancellationToken ct, bool tolerateFailure)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0 && !tolerateFailure)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr);

        return string.IsNullOrWhiteSpace(stdOut) ? stdErr : stdOut;
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static string GetTaskName(ScheduledJobDefinition job)
    {
        var prefix = job.Kind is ScheduledJobKind.Scan or ScheduledJobKind.ScanAndReport
            ? "StorageMaster Scan "
            : "StorageMaster Cleanup ";
        return prefix + job.Name;
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
}
