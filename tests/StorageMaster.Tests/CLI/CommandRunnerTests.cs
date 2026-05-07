using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Infrastructure;

namespace StorageMaster.Tests.CLI;

/// <summary>
/// Tests for CommandRunner — the headless CLI dispatcher.
/// All tests use in-memory TextWriters and mocked dependencies
/// so no WinUI shell or real filesystem operations are required.
/// </summary>
public sealed class CommandRunnerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (StringBuilder stdout, StringBuilder stderr) Capture(
        out TextWriter outWriter, out TextWriter errWriter)
    {
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();
        outWriter = new StringWriter(stdoutSb);
        errWriter = new StringWriter(stderrSb);
        return (stdoutSb, stderrSb);
    }

    private static CommandRunner BuildRunner(
        Mock<IFileScanner>? managedScanner = null,
        Mock<IFileScanner>? turboScanner = null,
        Mock<IAdminService>? admin = null,
        Mock<ISettingsRepository>? settings = null,
        Mock<IScanRepository>? scanRepo = null,
        Mock<ICleanupEngine>? cleanupEngine = null,
        Mock<IDuplicateFinderService>? dedupeSvc = null,
        Mock<IDuplicateRepository>? dupeRepo = null,
        Mock<IScheduledTaskService>? scheduler = null,
        Mock<IDriveHealthProvider>? healthProvider = null,
        Mock<IDriveHealthRepository>? healthRepo = null,
        Mock<ILocalDiagnosticsService>? diagnostics = null,
        string version = "9.0.0")
    {
        managedScanner ??= new Mock<IFileScanner>();
        turboScanner ??= new Mock<IFileScanner>();
        admin ??= new Mock<IAdminService>();
        settings ??= DefaultSettings();
        scanRepo ??= new Mock<IScanRepository>();
        cleanupEngine ??= new Mock<ICleanupEngine>();
        dedupeSvc ??= new Mock<IDuplicateFinderService>();
        dupeRepo ??= new Mock<IDuplicateRepository>();
        scheduler ??= new Mock<IScheduledTaskService>();
        healthProvider ??= new Mock<IDriveHealthProvider>();
        healthRepo ??= new Mock<IDriveHealthRepository>();
        diagnostics ??= DiagnosticsStub();

        return new CommandRunner(
            managedScanner.Object,
            turboScanner.Object,
            admin.Object,
            settings.Object,
            scanRepo.Object,
            cleanupEngine.Object,
            dedupeSvc.Object,
            dupeRepo.Object,
            scheduler.Object,
            healthProvider.Object,
            healthRepo.Object,
            diagnostics.Object,
            version,
            NullLogger<CommandRunner>.Instance);
    }

    private static Mock<ISettingsRepository> DefaultSettings(AppSettings? s = null)
    {
        var mock = new Mock<ISettingsRepository>();
        mock.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(s ?? new AppSettings { ScanParallelism = 4 });
        mock.Setup(r => r.SaveAsync(It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static Mock<ILocalDiagnosticsService> DiagnosticsStub()
    {
        var mock = new Mock<ILocalDiagnosticsService>();
        mock.Setup(d => d.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static ScanSession CompletedSession(long id = 1) => new()
    {
        Id = id,
        RootPath = Path.GetTempPath(),
        Status = ScanStatus.Completed,
        TotalFiles = 42,
        TotalFolders = 5,
        TotalSizeBytes = 1_000_000,
        StartedUtc = DateTime.UtcNow.AddMinutes(-2),
        CompletedUtc = DateTime.UtcNow,
    };

    // ── No-arg / unknown command ───────────────────────────────────────────────

    [Fact]
    public async Task NoArgs_PrintsUsageAndReturns2()
    {
        var (stdout, _) = Capture(out var outW, out var errW);
        var runner = BuildRunner();

        var code = await runner.RunAsync([], headless: true, outW, errW);

        code.Should().Be(2);
        stdout.ToString().Should().Contain("Usage:");
    }

    [Fact]
    public async Task UnknownCommand_Returns2()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var runner = BuildRunner();

        var code = await runner.RunAsync(["frobnicate"], headless: true, outW, errW);

        code.Should().Be(2);
        stderr.ToString().Should().Contain("Unknown command");
    }

    // ── version ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    public async Task VersionCommand_PrintsVersionAndReturns0(string cmd)
    {
        var (stdout, _) = Capture(out var outW, out var errW);
        var runner = BuildRunner(version: "2.1.3");

        var code = await runner.RunAsync([cmd], headless: true, outW, errW);

        code.Should().Be(0);
        stdout.ToString().Should().Contain("2.1.3");
    }

    // ── scan ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanCommand_MissingPath_Returns2()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var runner = BuildRunner();

        var code = await runner.RunAsync(["scan"], headless: true, outW, errW);

        code.Should().Be(2);
        stderr.ToString().Should().Contain("--path");
    }

    [Fact]
    public async Task ScanCommand_RelativePath_Returns2()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var runner = BuildRunner();

        var code = await runner.RunAsync(["scan", "--path", "relative\\path"], headless: true, outW, errW);

        code.Should().Be(2);
        stderr.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task ScanCommand_NonexistentPath_Returns2()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var runner = BuildRunner();
        var bogus = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}");

        var code = await runner.RunAsync(["scan", "--path", bogus], headless: true, outW, errW);

        code.Should().Be(2);
        stderr.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task ScanCommand_ValidPath_Returns0AndPrintsSessionInfo()
    {
        var (stdout, _) = Capture(out var outW, out var errW);
        var scanner = new Mock<IFileScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(CompletedSession(7));
        var runner = BuildRunner(managedScanner: scanner);

        var code = await runner.RunAsync(["scan", "--path", Path.GetTempPath()], headless: true, outW, errW);

        code.Should().Be(0);
        stdout.ToString().Should().Contain("Session 7");
    }

    [Fact]
    public async Task ScanCommand_DeepFlagWithoutAdmin_Returns4()
    {
        var (_, _) = Capture(out var outW, out var errW);
        var admin = new Mock<IAdminService>();
        admin.Setup(a => a.IsRunningAsAdmin).Returns(false);
        var runner = BuildRunner(admin: admin);

        var code = await runner.RunAsync(["scan", "--path", Path.GetTempPath(), "--deep"], headless: true, outW, errW);

        code.Should().Be(4, "deep scan requires admin; exit 4 is the non-admin sentinel");
    }

    [Fact]
    public async Task ScanCommand_DeepFlagAsAdmin_ExecutesScan()
    {
        var (stdout, _) = Capture(out var outW, out var errW);
        var admin = new Mock<IAdminService>();
        admin.Setup(a => a.IsRunningAsAdmin).Returns(true);
        var scanner = new Mock<IFileScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(CompletedSession());
        var runner = BuildRunner(managedScanner: scanner, admin: admin);

        var code = await runner.RunAsync(["scan", "--path", Path.GetTempPath(), "--deep"], headless: true, outW, errW);

        code.Should().Be(0);
        scanner.Verify(s => s.ScanAsync(It.Is<ScanOptions>(o => o.DeepScan), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanCommand_JsonOutput_WritesFileAndReturns0()
    {
        var jsonPath = Path.Combine(Path.GetTempPath(), $"smtest_scan_{Guid.NewGuid():N}.json");
        try
        {
            var scanner = new Mock<IFileScanner>();
            scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(CompletedSession(42));
            var runner = BuildRunner(managedScanner: scanner);
            var (_, _) = Capture(out var outW, out var errW);

            var code = await runner.RunAsync(["scan", "--path", Path.GetTempPath(), "--json", jsonPath], headless: true, outW, errW);

            code.Should().Be(0);
            File.Exists(jsonPath).Should().BeTrue();
            var json = await File.ReadAllTextAsync(jsonPath);
            json.Should().Contain("\"Id\"").And.Contain("42");
        }
        finally
        {
            if (File.Exists(jsonPath)) File.Delete(jsonPath);
        }
    }

    // ── report last-scan ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReportLastScan_NoCompletedSession_ThrowsAndReturns1()
    {
        var (_, _) = Capture(out var outW, out var errW);
        var scanRepo = new Mock<IScanRepository>();
        scanRepo.Setup(r => r.GetRecentSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        var runner = BuildRunner(scanRepo: scanRepo);

        var code = await runner.RunAsync(["report", "last-scan"], headless: true, outW, errW);

        code.Should().Be(1);
    }

    [Fact]
    public async Task ReportLastScan_WithCompletedSession_Returns0()
    {
        var (stdout, _) = Capture(out var outW, out var errW);
        var scanRepo = new Mock<IScanRepository>();
        scanRepo.Setup(r => r.GetRecentSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([CompletedSession(99)]);
        scanRepo.Setup(r => r.GetLargestFilesAsync(99, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        scanRepo.Setup(r => r.GetLargestFoldersAsync(99, It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        scanRepo.Setup(r => r.GetCategoryBreakdownAsync(99, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyDictionary<FileTypeCategory, (long Count, long Bytes)>)
                    new Dictionary<FileTypeCategory, (long Count, long Bytes)>());
        var runner = BuildRunner(scanRepo: scanRepo);

        var code = await runner.RunAsync(["report", "last-scan"], headless: true, outW, errW);

        code.Should().Be(0);
        stdout.ToString().Should().Contain("Last scan 99");
    }

    // ── cleanup execute ───────────────────────────────────────────────────────

    [Fact]
    public async Task CleanupExecute_WithoutConfirm_Returns3()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var runner = BuildRunner();

        var code = await runner.RunAsync(
            ["cleanup", "execute", "--session", "1", "--rules", "TempFiles", "--recycle-bin"],
            headless: true, outW, errW);

        code.Should().Be(3);
        stderr.ToString().Should().Contain("--confirm");
    }

    [Fact]
    public async Task CleanupExecute_BothModesSpecified_Returns2()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var runner = BuildRunner();

        var code = await runner.RunAsync(
            ["cleanup", "execute", "--session", "1", "--rules", "TempFiles", "--recycle-bin", "--quarantine", "--confirm"],
            headless: true, outW, errW);

        code.Should().Be(2);
        stderr.ToString().Should().Contain("exactly one");
    }

    [Fact]
    public async Task CleanupExecute_NeitherModeSpecified_Returns2()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var runner = BuildRunner();

        var code = await runner.RunAsync(
            ["cleanup", "execute", "--session", "1", "--rules", "TempFiles", "--confirm"],
            headless: true, outW, errW);

        code.Should().Be(2);
    }

    [Fact]
    public async Task CleanupExecute_NoMatchingRules_Returns2()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var engine = new Mock<ICleanupEngine>();
        engine.Setup(e => e.GetSuggestionsAsync(It.IsAny<long>(), It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
              .Returns(AsyncEmpty<CleanupSuggestion>());
        var runner = BuildRunner(cleanupEngine: engine);

        var code = await runner.RunAsync(
            ["cleanup", "execute", "--session", "1", "--rules", "NonExistentRule", "--recycle-bin", "--confirm"],
            headless: true, outW, errW);

        code.Should().Be(2);
        stderr.ToString().Should().Contain("No cleanup suggestions matched");
    }

    [Fact]
    public async Task CleanupExecute_AllSuccess_Returns0()
    {
        var suggestion = new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = "core.temp-files",
            Title = "Temp files",
            Description = "Temp",
            Category = CleanupCategory.TempFiles,
            Risk = CleanupRisk.Low,
            EstimatedBytes = 1000,
            TargetPaths = [Path.GetTempPath() + "dummy.tmp"],
        };
        var result = new CleanupResult
        {
            SuggestionId = suggestion.Id,
            Status = CleanupResultStatus.Success,
            BytesFreed = 1000,
            ExecutedUtc = DateTime.UtcNow,
            WasDryRun = false,
        };

        var engine = new Mock<ICleanupEngine>();
        engine.Setup(e => e.GetSuggestionsAsync(It.IsAny<long>(), It.IsAny<AppSettings>(), It.IsAny<CancellationToken>()))
              .Returns(AsyncYield(suggestion));
        engine.Setup(e => e.ExecuteAsync(
                It.IsAny<IReadOnlyList<CleanupSuggestion>>(),
                false,
                DeletionMethod.RecycleBin,
                null,
                It.IsAny<CancellationToken>()))
              .ReturnsAsync([result]);

        var runner = BuildRunner(cleanupEngine: engine);
        var (stdout, _) = Capture(out var outW, out var errW);

        var code = await runner.RunAsync(
            ["cleanup", "execute", "--session", "1", "--rules", "core.temp-files", "--recycle-bin", "--confirm"],
            headless: true, outW, errW);

        code.Should().Be(0);
        stdout.ToString().Should().Contain("1 succeeded");
    }

    // ── jobs run ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task JobsRun_UnknownJobId_Returns4()
    {
        var (_, _) = Capture(out var outW, out var errW);
        var scheduler = new Mock<IScheduledTaskService>();
        scheduler.Setup(s => s.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ScheduledJobDefinition?)null);
        var runner = BuildRunner(scheduler: scheduler);

        var code = await runner.RunAsync(["jobs", "run", "--id", "nonexistent-id"], headless: true, outW, errW);

        code.Should().Be(4);
    }

    // ── health report ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthReport_AllHealthy_Returns0()
    {
        var (stdout, _) = Capture(out var outW, out var errW);
        var snapshots = new List<DriveHealthSnapshot>
        {
            new() { DriveName = "C:\\", Status = DriveHealthStatus.Healthy, Message = "OK", Source = "WMI" },
        };
        var healthProvider = new Mock<IDriveHealthProvider>();
        healthProvider.Setup(h => h.GetHealthAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(snapshots);
        var healthRepo = new Mock<IDriveHealthRepository>();
        healthRepo.Setup(r => r.SaveSnapshotsAsync(It.IsAny<IReadOnlyList<DriveHealthSnapshot>>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        var runner = BuildRunner(healthProvider: healthProvider, healthRepo: healthRepo);

        var code = await runner.RunAsync(["health", "report"], headless: true, outW, errW);

        code.Should().Be(0);
        stdout.ToString().Should().Contain("Healthy");
    }

    [Fact]
    public async Task HealthReport_CriticalDrive_Returns1()
    {
        var (_, _) = Capture(out var outW, out var errW);
        var snapshots = new List<DriveHealthSnapshot>
        {
            new() { DriveName = "D:\\", Status = DriveHealthStatus.Critical, Message = "Failing", Source = "SMART" },
        };
        var healthProvider = new Mock<IDriveHealthProvider>();
        healthProvider.Setup(h => h.GetHealthAsync(It.IsAny<CancellationToken>()))
                      .ReturnsAsync(snapshots);
        var healthRepo = new Mock<IDriveHealthRepository>();
        healthRepo.Setup(r => r.SaveSnapshotsAsync(It.IsAny<IReadOnlyList<DriveHealthSnapshot>>(), It.IsAny<CancellationToken>()))
                  .Returns(Task.CompletedTask);
        var runner = BuildRunner(healthProvider: healthProvider, healthRepo: healthRepo);

        var code = await runner.RunAsync(["health", "report"], headless: true, outW, errW);

        code.Should().Be(1, "any critical drive should cause exit code 1");
    }

    // ── cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CancelledToken_Returns1AndWritesCancelMessage()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var scanner = new Mock<IFileScanner>();
        scanner.Setup(s => s.ScanAsync(It.IsAny<ScanOptions>(), It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new OperationCanceledException());
        var runner = BuildRunner(managedScanner: scanner);

        using var cts = new CancellationTokenSource();
        var code = await runner.RunAsync(["scan", "--path", Path.GetTempPath()], headless: true, outW, errW, cts.Token);

        code.Should().Be(1);
        stderr.ToString().Should().Contain("cancelled");
    }

    // ── ParseOptions: bad args ────────────────────────────────────────────────

    [Fact]
    public async Task ScanCommand_UnexpectedPositionalArg_Returns2()
    {
        var (_, stderr) = Capture(out var outW, out var errW);
        var runner = BuildRunner();

        // "extra" is not a --flag, should be rejected by ParseOptions
        var code = await runner.RunAsync(["scan", "extra", "--path", Path.GetTempPath()], headless: true, outW, errW);

        code.Should().Be(2);
        stderr.ToString().Should().Contain("Unexpected argument");
    }

    // ── Async helpers ─────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<T> AsyncEmpty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<T> AsyncYield<T>(T item)
    {
        await Task.CompletedTask;
        yield return item;
    }
}
