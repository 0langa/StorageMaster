using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

/// <summary>Unit tests for <see cref="CleanupEngine"/>.</summary>
public sealed class CleanupEngineTests
{
    private readonly Mock<IFileDeleter> _deleter = new();
    private readonly Mock<ICleanupLogRepository> _log = new();
    private readonly AppSettings _settings = new();

    public CleanupEngineTests()
    {
        // Audit log always succeeds.
        _log.Setup(l => l.LogResultAsync(
                It.IsAny<CleanupResult>(),
                It.IsAny<CleanupSuggestion>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // ── GetSuggestionsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetSuggestionsAsync_NoRules_YieldsNothing()
    {
        var engine = BuildEngine([]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in engine.GetSuggestionsAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSuggestionsAsync_SingleRuleWithTwoSuggestions_YieldsBoth()
    {
        var s1 = MakeSuggestion("rule.a", "Delete A");
        var s2 = MakeSuggestion("rule.a", "Delete B");
        var engine = BuildEngine([new StubRule(s1, s2)]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in engine.GetSuggestionsAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetSuggestionsAsync_MultipleRules_YieldsFromAll()
    {
        var s1 = MakeSuggestion("rule.a", "Rule A result");
        var s2 = MakeSuggestion("rule.b", "Rule B result");
        var engine = BuildEngine([new StubRule(s1), new StubRule(s2)]);

        var suggestions = new List<CleanupSuggestion>();
        await foreach (var s in engine.GetSuggestionsAsync(1, _settings))
            suggestions.Add(s);

        suggestions.Should().HaveCount(2);
        suggestions.Select(x => x.Title).Should().Contain(["Rule A result", "Rule B result"]);
    }

    // ── ExecuteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptySuggestions_ReturnsEmptyList()
    {
        var engine = BuildEngine([]);
        var results = await engine.ExecuteAsync([], dryRun: false, DeletionMethod.RecycleBin);

        results.Should().BeEmpty();
        _deleter.Verify(d => d.DeleteManyAsync(
            It.IsAny<IReadOnlyList<DeletionRequest>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AllSucceed_ReturnsSuccessStatus()
    {
        SetupDeleterSuccess();
        var suggestion = MakeSuggestion("rule.x", "Clean temps", [@"C:\Temp\file1.tmp"]);
        var engine = BuildEngine([]);

        var results = await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.RecycleBin);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(CleanupResultStatus.Success);
        results[0].BytesFreed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExecuteAsync_AllFail_ReturnsFailed()
    {
        SetupDeleterFailure();
        var suggestion = MakeSuggestion("rule.x", "Clean temps", [@"C:\Temp\locked.tmp"]);
        var engine = BuildEngine([]);

        var results = await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.RecycleBin);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(CleanupResultStatus.Failed);
        results[0].BytesFreed.Should().Be(0);
        results[0].FailedPaths.Should().Contain(@"C:\Temp\locked.tmp");
    }

    [Fact]
    public async Task ExecuteAsync_PartialFail_ReturnsPartialSuccess()
    {
        // First path succeeds, second fails.
        SetupDeleterMixed();
        var suggestion = MakeSuggestion("rule.x", "Mixed",
            [@"C:\Temp\ok.tmp", @"C:\Temp\locked.tmp"]);
        var engine = BuildEngine([]);

        var results = await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.RecycleBin);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(CleanupResultStatus.PartialSuccess);
        results[0].BytesFreed.Should().BeGreaterThan(0);
        results[0].FailedPaths.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_DryRun_CallsDeleterWithDryRunTrue()
    {
        SetupDeleterSuccess();
        var suggestion = MakeSuggestion("rule.x", "Dry run test", [@"C:\Temp\file.tmp"]);
        var engine = BuildEngine([]);

        await engine.ExecuteAsync([suggestion], dryRun: true, DeletionMethod.RecycleBin);

        _deleter.Verify(d => d.DeleteManyAsync(
            It.Is<IReadOnlyList<DeletionRequest>>(reqs => reqs.All(r => r.DryRun)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_HighRiskPermanentDelete_IsBlockedByEnginePolicy()
    {
        var suggestion = MakeSuggestion("rule.high", "High risk", [@"C:\Temp\danger.tmp"]) with
        {
            Risk = CleanupRisk.High,
        };
        var engine = BuildEngine([]);

        var results = await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.Permanent);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(CleanupResultStatus.Failed);
        results[0].ErrorMessage.Should().Contain("High-risk");
        _deleter.Verify(d => d.DeleteManyAsync(
            It.IsAny<IReadOnlyList<DeletionRequest>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UnsupportedQuarantine_IsBlockedByEnginePolicy()
    {
        var suggestion = MakeSuggestion("rule.no-quarantine", "No quarantine", [@"C:\Temp\file.tmp"]) with
        {
            SupportsQuarantine = false,
        };
        var engine = BuildEngine([]);

        var results = await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.Quarantine);

        results.Should().ContainSingle();
        results[0].Status.Should().Be(CleanupResultStatus.Failed);
        results[0].ErrorMessage.Should().Contain("does not support quarantine");
        _deleter.Verify(d => d.DeleteManyAsync(
            It.IsAny<IReadOnlyList<DeletionRequest>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Quarantine_RecordsQuarantinedPathsOnResult()
    {
        _deleter
            .Setup(d => d.DeleteManyAsync(
                It.IsAny<IReadOnlyList<DeletionRequest>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<DeletionRequest>, CancellationToken>(
                (reqs, _) => MakeQuarantineOutcomes(reqs));
        var suggestion = MakeSuggestion("rule.q", "Quarantine test", [@"C:\Temp\a.tmp", @"C:\Temp\b.tmp"]);
        var engine = BuildEngine([]);

        var results = await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.Quarantine);

        results.Should().ContainSingle();
        results[0].QuarantinedPaths.Should().HaveCount(2);
        results[0].QuarantinedPaths[0].OriginalPath.Should().Be(@"C:\Temp\a.tmp");
        results[0].QuarantinedPaths[0].QuarantinePath.Should().Be(@"Q:\quarantine\a.tmp");
    }

    [Fact]
    public async Task ExecuteAsync_Quarantine_WritesRestoreRecordsWithNullMemberId()
    {
        _deleter
            .Setup(d => d.DeleteManyAsync(
                It.IsAny<IReadOnlyList<DeletionRequest>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<DeletionRequest>, CancellationToken>(
                (reqs, _) => MakeQuarantineOutcomes(reqs));
        var recorder = new Mock<IQuarantineRecorder>();
        var suggestion = MakeSuggestion("rule.q", "Quarantine restore records", [@"C:\Temp\a.tmp"]);
        var engine = new CleanupEngine(
            [], _deleter.Object, _log.Object, NullLogger<CleanupEngine>.Instance, recorder.Object);

        await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.Quarantine);

        recorder.Verify(r => r.RecordQuarantineAsync(
            null,
            IQuarantineRecorder.GenericCleanupRunId,
            @"C:\Temp\a.tmp",
            @"Q:\quarantine\a.tmp",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunQuarantine_WritesNoRestoreRecords()
    {
        _deleter
            .Setup(d => d.DeleteManyAsync(
                It.IsAny<IReadOnlyList<DeletionRequest>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<DeletionRequest>, CancellationToken>(
                (reqs, _) => MakeQuarantineOutcomes(reqs));
        var recorder = new Mock<IQuarantineRecorder>();
        var suggestion = MakeSuggestion("rule.q", "Dry run", [@"C:\Temp\a.tmp"]);
        var engine = new CleanupEngine(
            [], _deleter.Object, _log.Object, NullLogger<CleanupEngine>.Instance, recorder.Object);

        await engine.ExecuteAsync([suggestion], dryRun: true, DeletionMethod.Quarantine);

        recorder.Verify(r => r.RecordQuarantineAsync(
            It.IsAny<long?>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_RestoreRecordFailure_DoesNotFailCleanup()
    {
        _deleter
            .Setup(d => d.DeleteManyAsync(
                It.IsAny<IReadOnlyList<DeletionRequest>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<DeletionRequest>, CancellationToken>(
                (reqs, _) => MakeQuarantineOutcomes(reqs));
        var recorder = new Mock<IQuarantineRecorder>();
        recorder.Setup(r => r.RecordQuarantineAsync(
                It.IsAny<long?>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("db unavailable"));
        var suggestion = MakeSuggestion("rule.q", "Record failure", [@"C:\Temp\a.tmp"]);
        var engine = new CleanupEngine(
            [], _deleter.Object, _log.Object, NullLogger<CleanupEngine>.Instance, recorder.Object);

        var results = await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.Quarantine);

        results[0].Status.Should().Be(CleanupResultStatus.Success,
            "the file was moved successfully; a missing restore record must not fail the cleanup");
        results[0].QuarantinedPaths.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteAsync_RecycleBin_LeavesQuarantinedPathsEmpty()
    {
        SetupDeleterSuccess();
        var suggestion = MakeSuggestion("rule.x", "Recycle test", [@"C:\Temp\a.tmp"]);
        var engine = BuildEngine([]);

        var results = await engine.ExecuteAsync([suggestion], dryRun: false, DeletionMethod.RecycleBin);

        results[0].QuarantinedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_LogsEachResultToAuditLog()
    {
        SetupDeleterSuccess();
        var s1 = MakeSuggestion("rule.a", "Cleanup A", [@"C:\Temp\a.tmp"]);
        var s2 = MakeSuggestion("rule.b", "Cleanup B", [@"C:\Temp\b.tmp"]);
        var engine = BuildEngine([]);

        await engine.ExecuteAsync([s1, s2], dryRun: false, DeletionMethod.RecycleBin);

        _log.Verify(l => l.LogResultAsync(
            It.IsAny<CleanupResult>(),
            It.IsAny<CleanupSuggestion>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_ReportsProgressForEachSuggestion()
    {
        SetupDeleterSuccess();
        var suggestions = new[]
        {
            MakeSuggestion("rule.a", "A", [@"C:\Temp\a.tmp"]),
            MakeSuggestion("rule.b", "B", [@"C:\Temp\b.tmp"]),
        };
        var engine = BuildEngine([]);

        var progressReports = new List<CleanupProgress>();
        var progress = new Progress<CleanupProgress>(p => progressReports.Add(p));

        await engine.ExecuteAsync([.. suggestions], dryRun: false,
            DeletionMethod.RecycleBin, progress);

        // Engine reports one update per suggestion plus a 100%-complete final report.
        progressReports.Should().HaveCountGreaterThanOrEqualTo(suggestions.Length);
        progressReports.Last().Completed.Should().Be(progressReports.Last().Total,
            "last progress report should signal 100% complete");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private CleanupEngine BuildEngine(IEnumerable<ICleanupRule> rules) =>
        new(rules, _deleter.Object, _log.Object, NullLogger<CleanupEngine>.Instance);

    private static CleanupSuggestion MakeSuggestion(
        string ruleId,
        string title,
        IReadOnlyList<string>? paths = null) => new()
        {
            Id = Guid.NewGuid(),
            RuleId = ruleId,
            Title = title,
            Description = "Test suggestion",
            Category = CleanupCategory.TempFiles,
            Risk = CleanupRisk.Low,
            EstimatedBytes = 1024,
            TargetPaths = paths ?? [@"C:\Temp\test.tmp"],
            IsSystemPath = false,
        };

    private void SetupDeleterSuccess() =>
        _deleter
            .Setup(d => d.DeleteManyAsync(
                It.IsAny<IReadOnlyList<DeletionRequest>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<DeletionRequest>, CancellationToken>(
                (reqs, _) => MakeSuccessOutcomes(reqs));

    private void SetupDeleterFailure() =>
        _deleter
            .Setup(d => d.DeleteManyAsync(
                It.IsAny<IReadOnlyList<DeletionRequest>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<DeletionRequest>, CancellationToken>(
                (reqs, _) => MakeFailOutcomes(reqs));

    private void SetupDeleterMixed() =>
        _deleter
            .Setup(d => d.DeleteManyAsync(
                It.IsAny<IReadOnlyList<DeletionRequest>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<DeletionRequest>, CancellationToken>(
                (reqs, _) => MakeMixedOutcomes(reqs));

    private static async IAsyncEnumerable<DeletionOutcome> MakeSuccessOutcomes(
        IReadOnlyList<DeletionRequest> reqs)
    {
        foreach (var r in reqs)
            yield return new DeletionOutcome(r.Path, Success: true, BytesFreed: 1024L);
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<DeletionOutcome> MakeFailOutcomes(
        IReadOnlyList<DeletionRequest> reqs)
    {
        foreach (var r in reqs)
            yield return new DeletionOutcome(r.Path, Success: false, BytesFreed: 0, Error: "Access denied");
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<DeletionOutcome> MakeQuarantineOutcomes(
        IReadOnlyList<DeletionRequest> reqs)
    {
        foreach (var r in reqs)
        {
            yield return new DeletionOutcome(
                r.Path, Success: true, BytesFreed: 1024L,
                QuarantinePath: @"Q:\quarantine\" + Path.GetFileName(r.Path));
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<DeletionOutcome> MakeMixedOutcomes(
        IReadOnlyList<DeletionRequest> reqs)
    {
        bool first = true;
        foreach (var r in reqs)
        {
            if (first)
            {
                yield return new DeletionOutcome(r.Path, Success: true, BytesFreed: 1024L);
                first = false;
            }
            else
            {
                yield return new DeletionOutcome(r.Path, Success: false, BytesFreed: 0, Error: "Locked");
            }
        }
        await Task.CompletedTask;
    }

    // ── Stub rule for controlled output ───────────────────────────────────────

    private sealed class StubRule : ICleanupRule
    {
        private readonly CleanupSuggestion[] _suggestions;
        public string RuleId => "test.stub";
        public string DisplayName => "Stub";
        public CleanupCategory Category => CleanupCategory.TempFiles;

        public StubRule(params CleanupSuggestion[] suggestions)
            => _suggestions = suggestions;

        public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
            long sessionId, AppSettings settings,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var s in _suggestions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return s;
            }
            await Task.CompletedTask;
        }
    }
}
