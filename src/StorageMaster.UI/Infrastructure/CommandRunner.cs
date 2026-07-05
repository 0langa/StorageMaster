using System.Text.Json;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;
using StorageMaster.Platform.Windows;

namespace StorageMaster.UI.Infrastructure;

public sealed class CommandRunner(
    FileScanner managedScanner,
    TurboFileScanner turboScanner,
    IAdminService adminService,
    ISettingsRepository settingsRepository,
    IScanRepository scanRepository,
    ICleanupEngine cleanupEngine,
    IDuplicateFinderService duplicateFinderService,
    IDuplicateRepository duplicateRepository,
    IScheduledTaskService scheduledTaskService,
    IDriveHealthProvider driveHealthProvider,
    IDriveHealthRepository driveHealthRepository,
    ILocalDiagnosticsService diagnostics,
    string currentVersion,
    ILogger<CommandRunner> logger) : ICommandRunner
{
    public async Task<int> RunAsync(
        string[] args,
        bool headless,
        TextWriter output,
        TextWriter error,
        CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            await WriteUsageAsync(output);
            return 2;
        }

        try
        {
            var exitCode = await DispatchAsync(args, headless, output, error, ct);
            await diagnostics.RecordAsync("headless", $"exit|{exitCode}|{string.Join(' ', args)}", ct);
            return exitCode;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Operation cancelled.");
            await diagnostics.RecordAsync("headless", $"cancelled|{string.Join(' ', args)}", ct);
            return 1;
        }
        catch (CommandLineException ex)
        {
            await error.WriteLineAsync(ex.Message);
            await WriteUsageAsync(error);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command execution failed");
            await error.WriteLineAsync(ex.Message);
            await diagnostics.RecordAsync("headless", $"error|{string.Join(' ', args)}|{ex.Message}", ct);
            return 1;
        }
    }

    private async Task<int> DispatchAsync(
        string[] args,
        bool headless,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        if (args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
            return await RunScanAsync(args, output, ct);

        if (args[0].Equals("version", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("--version", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync($"StorageMaster {currentVersion}");
            return 0;
        }

        if (args.Length >= 2 &&
            args[0].Equals("report", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("last-scan", StringComparison.OrdinalIgnoreCase))
            return await RunLastScanReportAsync(args[2..], output, ct);

        if (args.Length >= 2 &&
            args[0].Equals("dedupe", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("scan", StringComparison.OrdinalIgnoreCase))
            return await RunDedupeScanAsync(args[2..], output, ct);

        if (args.Length >= 2 &&
            args[0].Equals("cleanup", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("analyze", StringComparison.OrdinalIgnoreCase))
            return await RunCleanupAnalyzeAsync(args[2..], output, ct);

        if (args.Length >= 2 &&
            args[0].Equals("cleanup", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("execute", StringComparison.OrdinalIgnoreCase))
            return await RunCleanupExecuteAsync(args[2..], output, error, ct);

        if (args.Length >= 3 &&
            args[0].Equals("jobs", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("run", StringComparison.OrdinalIgnoreCase))
            return await RunScheduledJobAsync(args[2..], headless, output, ct);

        if (args.Length >= 2 &&
            args[0].Equals("health", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("report", StringComparison.OrdinalIgnoreCase))
            return await RunHealthReportAsync(args[2..], output, ct);

        throw new CommandLineException("Unknown command.", 2);
    }

    private async Task<int> RunScanAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var options = ParseOptions(args[1..]);
        var path = GetRequiredOption(options, "--path");
        var turbo = options.ContainsKey("--turbo");
        var deep = options.ContainsKey("--deep");
        var jsonPath = GetOptionalOption(options, "--json");

        if (!Path.IsPathRooted(path) || !Directory.Exists(path))
            throw new CommandLineException("Scan path must be an existing absolute path.", 2);

        if (deep && !adminService.IsRunningAsAdmin)
        {
            await output.WriteLineAsync("Deep scan requires administrator privileges. Re-run from an elevated prompt.");
            return 4;
        }

        var settings = await settingsRepository.LoadAsync(ct);
        var scanOptions = new ScanOptions
        {
            RootPath = path,
            MaxParallelism = Math.Clamp(settings.ScanParallelism, 1, 16),
            DbBatchSize = 500,
            FollowSymlinks = false,
            IncludeHiddenFiles = settings.ShowHiddenFiles || deep,
            DeepScan = deep,
            ExcludedPaths = ScanScopeResolver.BuildExcludedPaths(settings, deep),
        };

        var activeScanner = turbo && TurboFileScanner.IsAvailable
            ? (IFileScanner)turboScanner
            : managedScanner;

        await output.WriteLineAsync($"Scanning {path}...");
        var session = await activeScanner.ScanAsync(scanOptions, new Progress<ScanProgress>(), ct);

        var payload = new
        {
            session.Id,
            session.RootPath,
            Status = session.Status.ToString(),
            session.TotalFiles,
            session.TotalFolders,
            session.TotalSizeBytes,
            session.AccessDeniedCount,
            session.StartedUtc,
            session.CompletedUtc,
        };

        await MaybeWriteJsonAsync(jsonPath, payload, ct);
        await output.WriteLineAsync($"Scan complete. Session {session.Id}, {session.TotalFiles:N0} files, {session.TotalSizeBytes:N0} bytes.");
        return 0;
    }

    private async Task<int> RunLastScanReportAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var options = ParseOptions(args);
        var jsonPath = GetOptionalOption(options, "--json");
        var csvPath = GetOptionalOption(options, "--csv");
        var session = (await scanRepository.GetRecentSessionsAsync(20, ct))
            .FirstOrDefault(static item => item.Status == ScanStatus.Completed);
        if (session is null)
            throw new InvalidOperationException("No completed scan session is available.");

        var largestFiles = await scanRepository.GetLargestFilesAsync(session.Id, 25, ct);
        var largestFolders = await scanRepository.GetLargestFoldersAsync(session.Id, 25, ct);
        var categories = await scanRepository.GetCategoryBreakdownAsync(session.Id, ct);
        var payload = new
        {
            session.Id,
            session.RootPath,
            Status = session.Status.ToString(),
            session.TotalFiles,
            session.TotalFolders,
            session.TotalSizeBytes,
            Categories = categories.Select(static item => new
            {
                Category = item.Key.ToString(),
                Count = item.Value.Count,
                Bytes = item.Value.Bytes,
            }),
            LargestFiles = largestFiles.Select(static file => new { file.FullPath, file.SizeBytes, Category = file.Category.ToString() }),
            LargestFolders = largestFolders.Select(static folder => new { folder.FullPath, folder.TotalSizeBytes, folder.FileCount }),
        };

        await MaybeWriteJsonAsync(jsonPath, payload, ct);
        if (!string.IsNullOrWhiteSpace(csvPath))
            await WriteReportCsvAsync(csvPath, largestFiles, largestFolders, ct);

        await output.WriteLineAsync($"Last scan {session.Id}: {session.RootPath} - {session.TotalFiles:N0} files, {session.TotalSizeBytes:N0} bytes.");
        return 0;
    }

    private async Task<int> RunDedupeScanAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var options = ParseOptions(args);
        if (!long.TryParse(GetRequiredOption(options, "--session"), out var sessionId))
            throw new CommandLineException("--session must be a valid integer.", 2);
        var methods = ParseMethods(GetRequiredOption(options, "--methods"));
        var minSizeMb = int.TryParse(GetOptionalOption(options, "--min-size"), out var parsedMinSize) ? Math.Max(0, parsedMinSize) : 1;
        var extensions = ParseCsv(GetOptionalOption(options, "--extensions"))
            .Select(static value => value.StartsWith('.') ? value.ToLowerInvariant() : "." + value.ToLowerInvariant())
            .ToList();
        var jsonPath = GetOptionalOption(options, "--json");
        var settings = await settingsRepository.LoadAsync(ct);

        await output.WriteLineAsync($"Running duplicate analysis for session {sessionId}...");
        var run = await duplicateFinderService.RunAsync(new DuplicateScanOptions
        {
            SessionId = sessionId,
            MinimumSizeBytes = minSizeMb * 1024L * 1024L,
            Methods = methods,
            IncludeExtensions = extensions,
            KeeperPolicy = settings.DuplicateKeeperPolicy,
            MaxConcurrency = Math.Max(1, settings.ScanParallelism),
            PerDriveConcurrency = Math.Max(1, Math.Min(4, settings.ScanParallelism / 2)),
            ExcludedPaths = settings.ExcludedPaths.ToList(),
            IncludeHiddenFiles = settings.ShowHiddenFiles,
        }, null, ct);

        var summary = await duplicateRepository.GetDuplicateRunSummaryAsync(run.Id, ct);
        var payload = new
        {
            run.Id,
            run.SessionId,
            Status = run.Status.ToString(),
            run.StartedUtc,
            run.CompletedUtc,
            run.GroupCount,
            run.ReclaimableBytes,
            run.ErrorCount,
            summary.ExactGroupCount,
            summary.ReviewGroupCount,
        };
        await MaybeWriteJsonAsync(jsonPath, payload, ct);
        await output.WriteLineAsync($"Dedupe run {run.Id}: {summary.GroupCount:N0} groups, {summary.ReclaimableBytes:N0} reclaimable bytes.");
        return run.Status == DuplicateRunStatus.Completed ? 0 : 1;
    }

    private async Task<int> RunCleanupAnalyzeAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var options = ParseOptions(args);
        if (!long.TryParse(GetRequiredOption(options, "--session"), out var sessionId))
            throw new CommandLineException("--session must be a valid integer.", 2);
        var jsonPath = GetOptionalOption(options, "--json");
        var settings = await settingsRepository.LoadAsync(ct);
        var suggestions = await CollectSuggestionsAsync(sessionId, settings, ct);
        var payload = new
        {
            SessionId = sessionId,
            SuggestionCount = suggestions.Count,
            EstimatedBytes = suggestions.Sum(static item => item.EstimatedBytes),
            Suggestions = suggestions.Select(static item => new
            {
                item.RuleId,
                item.Title,
                Category = item.Category.ToString(),
                Risk = item.Risk.ToString(),
                item.EstimatedBytes,
            }),
        };

        await MaybeWriteJsonAsync(jsonPath, payload, ct);
        await output.WriteLineAsync($"Cleanup analysis for session {sessionId}: {suggestions.Count:N0} suggestion(s).");
        return 0;
    }

    private async Task<int> RunCleanupExecuteAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var options = ParseOptions(args);
        if (!options.ContainsKey("--confirm"))
        {
            await error.WriteLineAsync("Cleanup execution requires --confirm.");
            return 3;
        }

        if (!long.TryParse(GetRequiredOption(options, "--session"), out var sessionId))
            throw new CommandLineException("--session must be a valid integer.", 2);
        var rules = ParseCsv(GetRequiredOption(options, "--rules"));
        var useRecycleBin = options.ContainsKey("--recycle-bin");
        var useQuarantine = options.ContainsKey("--quarantine");
        if (useRecycleBin == useQuarantine)
            throw new CommandLineException("Choose exactly one cleanup deletion mode: --recycle-bin or --quarantine.", 2);

        var settings = await settingsRepository.LoadAsync(ct);
        var suggestions = await CollectSuggestionsAsync(sessionId, settings, ct);
        var selected = suggestions.Where(item => MatchesRule(item, rules)).ToList();
        if (selected.Count == 0)
            throw new CommandLineException("No cleanup suggestions matched the supplied --rules filter.", 2);

        var method = useRecycleBin ? DeletionMethod.RecycleBin : DeletionMethod.Quarantine;
        if (method == DeletionMethod.Quarantine && selected
                .SelectMany(static suggestion => suggestion.TargetPaths)
                .Any(static path => path.StartsWith("::", StringComparison.Ordinal) || Directory.Exists(path)))
        {
            throw new CommandLineException(
                "Cleanup quarantine supports file targets only. Use --recycle-bin for general cleanup directories.",
                2);
        }

        var results = await cleanupEngine.ExecuteAsync(selected, dryRun: false, method, null, ct);
        await output.WriteLineAsync($"Cleanup executed. {results.Count(static item => item.Status == CleanupResultStatus.Success):N0} succeeded.");

        var quarantinedCount = results.Sum(static item => item.QuarantinedPaths.Count);
        if (quarantinedCount > 0)
        {
            var quarantineRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StorageMaster", "Quarantine");
            await output.WriteLineAsync(
                $"Quarantined {quarantinedCount:N0} file(s) under {quarantineRoot}. " +
                "Restore records were written; use the app's \"All quarantined files\" section on the Duplicates page for one-click restore. " +
                "Original and quarantine paths are also recorded in the CleanupLog audit trail.");
        }

        return results.All(static item => item.Status == CleanupResultStatus.Success) ? 0 : 1;
    }

    private async Task<int> RunScheduledJobAsync(string[] args, bool headless, TextWriter output, CancellationToken ct)
    {
        var options = ParseOptions(args);
        var jobId = GetRequiredOption(options, "--id");
        var job = await scheduledTaskService.GetJobAsync(jobId, ct);
        if (job is null)
            return 4;

        try
        {
            switch (job.Kind)
            {
                case ScheduledJobKind.Scan:
                    await RunScanJobAsync(job, output, ct);
                    break;
                case ScheduledJobKind.ScanAndReport:
                    await RunScanAndReportJobAsync(job, output, ct);
                    break;
                case ScheduledJobKind.CleanupAnalyze:
                    await RunCleanupAnalyzeJobAsync(job, output, ct);
                    break;
                case ScheduledJobKind.CleanupExecuteSafe:
                    await RunCleanupExecuteJobAsync(job, output, ct);
                    break;
            }

            await scheduledTaskService.UpdateRunOutcomeAsync(job.Id, "Success", "Completed successfully.", ct);
            if (headless)
                await output.WriteLineAsync($"Scheduled job {job.Name} completed.");
            return 0;
        }
        catch (Exception ex)
        {
            await scheduledTaskService.UpdateRunOutcomeAsync(job.Id, "Failed", ex.Message, ct);
            throw;
        }
    }

    private async Task<int> RunHealthReportAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var options = ParseOptions(args);
        var jsonPath = GetOptionalOption(options, "--json");
        var snapshots = await driveHealthProvider.GetHealthAsync(ct);
        await driveHealthRepository.SaveSnapshotsAsync(snapshots, ct);

        var payload = new
        {
            CapturedUtc = DateTime.UtcNow,
            Drives = snapshots.Select(static snapshot => new
            {
                snapshot.DriveName,
                snapshot.VolumeLabel,
                snapshot.DriveFormat,
                snapshot.TotalBytes,
                snapshot.FreeBytes,
                snapshot.FreePercent,
                Status = snapshot.Status.ToString(),
                snapshot.Source,
                snapshot.Message,
                snapshot.Model,
                snapshot.SerialNumber,
                snapshot.MediaType,
                snapshot.TemperatureCelsius,
                snapshot.WearPercent,
                snapshot.CapturedUtc,
            }),
        };

        await MaybeWriteJsonAsync(jsonPath, payload, ct);
        foreach (var snapshot in snapshots)
            await output.WriteLineAsync($"{snapshot.DriveName}: {snapshot.Status} - {snapshot.Message}");
        return snapshots.Any(static snapshot => snapshot.Status == DriveHealthStatus.Critical) ? 1 : 0;
    }

    private async Task RunScanJobAsync(ScheduledJobDefinition job, TextWriter output, CancellationToken ct)
    {
        await RunNestedCommandAsync(["scan", "--path", job.TargetPath], output, ct);
    }

    private async Task RunScanAndReportJobAsync(ScheduledJobDefinition job, TextWriter output, CancellationToken ct)
    {
        await RunScanJobAsync(job, output, ct);
        var exportDirectory = EnsureExportsDirectory();
        var jsonPath = Path.Combine(exportDirectory, $"scheduled-report-{job.Id}.json");
        await RunNestedCommandAsync(["report", "last-scan", "--json", jsonPath], output, ct);
    }

    private async Task RunCleanupAnalyzeJobAsync(ScheduledJobDefinition job, TextWriter output, CancellationToken ct)
    {
        var session = await RunScanForJobAsync(job, ct);
        var exportDirectory = EnsureExportsDirectory();
        var jsonPath = Path.Combine(exportDirectory, $"scheduled-cleanup-{job.Id}.json");
        await RunNestedCommandAsync(["cleanup", "analyze", "--session", session.Id.ToString(), "--json", jsonPath], output, ct);
    }

    private async Task RunCleanupExecuteJobAsync(ScheduledJobDefinition job, TextWriter output, CancellationToken ct)
    {
        var session = await RunScanForJobAsync(job, ct);
        var rules = string.IsNullOrWhiteSpace(job.RulesCsv)
            ? "TempFiles,CacheFolders,BrowserCache,WindowsUpdateCache,DeliveryOptimization,WindowsErrorReporting,DownloadedInstallers"
            : job.RulesCsv;
        await RunNestedCommandAsync(
            ["cleanup", "execute", "--session", session.Id.ToString(), "--rules", rules, "--recycle-bin", "--confirm"],
            output,
            ct);
    }

    private async Task RunNestedCommandAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var exitCode = await RunAsync(args, true, output, TextWriter.Null, ct);
        if (exitCode != 0)
            throw new CommandLineException($"Nested command failed with exit code {exitCode}: {string.Join(' ', args)}", exitCode);
    }

    private async Task<ScanSession> RunScanForJobAsync(ScheduledJobDefinition job, CancellationToken ct)
    {
        var settings = await settingsRepository.LoadAsync(ct);
        return await managedScanner.ScanAsync(new ScanOptions
        {
            RootPath = job.TargetPath,
            MaxParallelism = Math.Clamp(settings.ScanParallelism, 1, 16),
            DbBatchSize = 500,
            FollowSymlinks = false,
            IncludeHiddenFiles = settings.ShowHiddenFiles,
            ExcludedPaths = ScanScopeResolver.BuildExcludedPaths(settings, deepScan: false),
        }, new Progress<ScanProgress>(), ct);
    }

    private static IReadOnlyList<DuplicateMethod> ParseMethods(string text)
    {
        var methods = new List<DuplicateMethod>();
        foreach (var token in ParseCsv(text))
        {
            methods.Add(token.ToLowerInvariant() switch
            {
                "exact" => DuplicateMethod.ExactSha256,
                "text" => DuplicateMethod.NormalizedText,
                "image" => DuplicateMethod.ImagePHash,
                "video" => DuplicateMethod.VideoPHash,
                _ => throw new CommandLineException($"Unsupported duplicate method '{token}'.", 2),
            });
        }

        return methods.Distinct().ToList();
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new CommandLineException($"Unexpected argument '{token}'.", 2);

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[token] = args[++i];
            }
            else
            {
                options[token] = null;
            }
        }

        return options;
    }

    private static IReadOnlyList<string> ParseCsv(string? text) =>
        (text ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string GetRequiredOption(IReadOnlyDictionary<string, string?> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
            throw new CommandLineException($"Missing required option {name}.", 2);
        return value;
    }

    private static string? GetOptionalOption(IReadOnlyDictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) ? value : null;

    private static bool MatchesRule(CleanupSuggestion suggestion, IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (string.Equals(suggestion.RuleId, token, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(suggestion.Category.ToString(), token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task MaybeWriteJsonAsync(string? path, object payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        }), ct);
    }

    private static async Task WriteReportCsvAsync(
        string csvPath,
        IReadOnlyList<FileEntry> files,
        IReadOnlyList<FolderEntry> folders,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync("Kind,Path,SizeBytes,CategoryOrCount");
        foreach (var file in files)
            await writer.WriteLineAsync($"File,\"{file.FullPath.Replace("\"", "\"\"", StringComparison.Ordinal)}\",{file.SizeBytes},{file.Category}");
        foreach (var folder in folders)
            await writer.WriteLineAsync($"Folder,\"{folder.FullPath.Replace("\"", "\"\"", StringComparison.Ordinal)}\",{folder.TotalSizeBytes},{folder.FileCount}");
    }

    private static string EnsureExportsDirectory()
    {
        var exportDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageMaster",
            "exports");
        Directory.CreateDirectory(exportDirectory);
        return exportDirectory;
    }

    private async Task<IReadOnlyList<CleanupSuggestion>> CollectSuggestionsAsync(
        long sessionId,
        AppSettings settings,
        CancellationToken ct)
    {
        var suggestions = new List<CleanupSuggestion>();
        await foreach (var suggestion in cleanupEngine.GetSuggestionsAsync(sessionId, settings, ct))
            suggestions.Add(suggestion);
        return suggestions;
    }

    private Task WriteUsageAsync(TextWriter output) =>
        output.WriteLineAsync(
            """
            Usage:
              StorageMaster.UI.exe --cli scan --path <abs-path> [--turbo] [--deep] [--json <file>]
              StorageMaster.UI.exe --cli report last-scan [--json <file>] [--csv <file>]
              StorageMaster.UI.exe --cli dedupe scan --session <id> --methods exact,text,image,video --min-size <mb> [--extensions ...] [--json <file>]
              StorageMaster.UI.exe --cli cleanup analyze --session <id> [--json <file>]
              StorageMaster.UI.exe --cli cleanup execute --session <id> --rules <csv> --recycle-bin|--quarantine --confirm
              StorageMaster.UI.exe --cli health report [--json <file>]
              StorageMaster.UI.exe --cli version
            """);
}

internal sealed class CommandLineException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
