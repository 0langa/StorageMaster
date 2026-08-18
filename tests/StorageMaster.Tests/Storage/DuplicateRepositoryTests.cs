using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

public sealed class DuplicateRepositoryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly ScanRepository _scanRepository;
    private readonly DuplicateRepository _repo;

    public DuplicateRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dup_repo_{Guid.NewGuid():N}.db");
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _scanRepository = new ScanRepository(_ctx);
        _repo = new DuplicateRepository(_ctx);
    }

    [Fact]
    public async Task GetCandidatesAsync_NormalizedQuery_DoesNotRequireSameSizeBucket()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        var modifiedUtc = new DateTime(2025, 8, 17, 12, 34, 56, DateTimeKind.Utc).AddTicks(7_654_321);
        var firstFile = MakeEntry(session.Id, @"C:\scope\a.txt", 10, FileTypeCategory.Document) with
        {
            CreatedUtc = modifiedUtc.AddDays(-2),
            ModifiedUtc = modifiedUtc,
            AccessedUtc = modifiedUtc.AddDays(1),
        };
        await _scanRepository.InsertFileEntriesAsync([
            firstFile,
            MakeEntry(session.Id, @"C:\scope\b.txt", 12, FileTypeCategory.Document),
        ]);

        var exactCandidates = await _repo.GetCandidatesAsync(new DuplicateCandidateQuery
        {
            SessionId = session.Id,
            MinimumSizeBytes = 0,
            RequireSameSizeBucket = true,
            Extensions = [".txt"],
        });
        var normalizedCandidates = await _repo.GetCandidatesAsync(new DuplicateCandidateQuery
        {
            SessionId = session.Id,
            MinimumSizeBytes = 0,
            RequireSameSizeBucket = false,
            Extensions = [".txt"],
        });

        exactCandidates.Should().BeEmpty("same-size SQL bucketing should exclude non-matching raw sizes");
        normalizedCandidates.Should().HaveCount(2, "normalized text must see both candidates even when raw sizes differ");
        var loadedCandidate = normalizedCandidates.Single(static candidate => candidate.File.FullPath == @"C:\scope\a.txt");
        var loadedFirst = loadedCandidate.File;
        AssertUtcTimestamp(loadedFirst.CreatedUtc, firstFile.CreatedUtc);
        AssertUtcTimestamp(loadedFirst.ModifiedUtc, firstFile.ModifiedUtc);
        AssertUtcTimestamp(loadedFirst.AccessedUtc, firstFile.AccessedUtc);
        loadedFirst.Identity.Should().Be(firstFile.Identity);
        loadedCandidate.Identity.Should().Be(firstFile.Identity);
    }

    [Fact]
    public async Task GetCandidatesAsync_RespectsIncludedExcludedAndHiddenFilters()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        await _scanRepository.InsertFileEntriesAsync([
            MakeEntry(session.Id, @"C:\scope\docs\visible.txt", 100, FileTypeCategory.Document),
            MakeEntry(session.Id, @"C:\scope\docs\skip\hidden.txt", 100, FileTypeCategory.Document, FileAttributes.Hidden),
            MakeEntry(session.Id, @"C:\scope\other\outside.txt", 100, FileTypeCategory.Document),
            MakeEntry(session.Id, @"C:\scope\docs\skip\visible.txt", 100, FileTypeCategory.Document),
        ]);

        var candidates = await _repo.GetCandidatesAsync(new DuplicateCandidateQuery
        {
            SessionId = session.Id,
            MinimumSizeBytes = 0,
            RequireSameSizeBucket = false,
            Extensions = [".txt"],
            IncludedPaths = [@"C:\scope\docs"],
            ExcludedPaths = [@"C:\scope\docs\skip"],
            IncludeHiddenFiles = false,
        });

        candidates.Select(static c => c.File.FullPath).Should().BeEquivalentTo([@"C:\scope\docs\visible.txt"]);
    }

    [Fact]
    public async Task GetCandidatesAsync_PathScopesRespectBoundariesAndLiteralWildcards()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        await _scanRepository.InsertFileEntriesAsync([
            MakeEntry(session.Id, @"C:\scope\docs\included.txt", 100, FileTypeCategory.Document),
            MakeEntry(session.Id, @"C:\scope\docs-old\sibling.txt", 100, FileTypeCategory.Document),
            MakeEntry(session.Id, @"C:\scope\100%\included.txt", 100, FileTypeCategory.Document),
            MakeEntry(session.Id, @"C:\scope\100X\sibling.txt", 100, FileTypeCategory.Document),
            MakeEntry(session.Id, @"C:\scope\under_score\included.txt", 100, FileTypeCategory.Document),
            MakeEntry(session.Id, @"C:\scope\underXscore\sibling.txt", 100, FileTypeCategory.Document),
        ]);

        var included = await _repo.GetCandidatesAsync(new DuplicateCandidateQuery
        {
            SessionId = session.Id,
            MinimumSizeBytes = 0,
            RequireSameSizeBucket = false,
            IncludedPaths = [@"c:\SCOPE\DOCS", @"C:\scope\100%", @"C:\scope\under_score"],
        });
        var excluded = await _repo.GetCandidatesAsync(new DuplicateCandidateQuery
        {
            SessionId = session.Id,
            MinimumSizeBytes = 0,
            RequireSameSizeBucket = false,
            IncludedPaths = [@"C:\scope"],
            ExcludedPaths = [@"C:\scope\docs", @"C:\scope\100%", @"C:\scope\under_score"],
        });

        included.Select(static c => c.File.FullPath).Should().BeEquivalentTo([
            @"C:\scope\docs\included.txt",
            @"C:\scope\100%\included.txt",
            @"C:\scope\under_score\included.txt",
        ]);
        excluded.Select(static c => c.File.FullPath).Should().BeEquivalentTo([
            @"C:\scope\docs-old\sibling.txt",
            @"C:\scope\100X\sibling.txt",
            @"C:\scope\underXscore\sibling.txt",
        ]);
    }

    [Fact]
    public async Task GetCandidatesAsync_CategoryFilter_UsesRealCategoryValues()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        await _scanRepository.InsertFileEntriesAsync([
            MakeEntry(session.Id, @"C:\scope\a.jpg", 100, FileTypeCategory.Image),
            MakeEntry(session.Id, @"C:\scope\b.mp4", 200, FileTypeCategory.Video),
            MakeEntry(session.Id, @"C:\scope\c.txt", 50, FileTypeCategory.Document),
        ]);

        var candidates = await _repo.GetCandidatesAsync(new DuplicateCandidateQuery
        {
            SessionId = session.Id,
            MinimumSizeBytes = 0,
            RequireSameSizeBucket = false,
            Categories = [FileTypeCategory.Image, FileTypeCategory.Video],
        });

        candidates.Select(static c => c.File.Category)
            .Should()
            .BeEquivalentTo([FileTypeCategory.Image, FileTypeCategory.Video]);
    }

    [Fact]
    public async Task SaveResultsAsync_UpsertsExistingSignature()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        await _scanRepository.InsertFileEntriesAsync([MakeEntry(session.Id, @"C:\scope\a.bin", 512, FileTypeCategory.Unknown)]);
        var file = (await _scanRepository.GetLargestFilesAsync(session.Id, 1)).Single();

        var run1 = await _repo.CreateRunAsync(new DuplicateScanOptions { SessionId = session.Id });
        var run2 = await _repo.CreateRunAsync(new DuplicateScanOptions { SessionId = session.Id });

        var firstSignature = MakeSignature(file, "hash-a", 1);
        var secondSignature = MakeSignature(file, "hash-b", 2);
        await _repo.SaveResultsAsync(run1.Id, [firstSignature], [], [], []);
        await _repo.SaveResultsAsync(run2.Id, [secondSignature], [], [], []);

        var cached = await _repo.GetCachedSignaturesAsync(session.Id, DuplicateMethod.ExactSha256, "SHA-256", 1);

        cached.Should().ContainSingle();
        cached[0].SignatureText.Should().Be("hash-b");
        cached[0].MetadataJson.Should().Contain("\"revision\":2");
        AssertUtcTimestamp(cached[0].ComputedUtc, secondSignature.ComputedUtc);
        AssertUtcTimestamp(cached[0].SourceModifiedUtc, secondSignature.SourceModifiedUtc);

        var loadedRuns = await _repo.GetRunsForSessionAsync(session.Id);
        var loadedRun2 = loadedRuns.Single(run => run.Id == run2.Id);
        AssertUtcTimestamp(loadedRun2.StartedUtc, run2.StartedUtc);
    }

    [Fact]
    public async Task QuarantineLookup_RoundTripsById()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        await _scanRepository.InsertFileEntriesAsync([MakeEntry(session.Id, @"C:\scope\keep.bin", 512, FileTypeCategory.Unknown)]);
        var file = (await _scanRepository.GetLargestFilesAsync(session.Id, 1)).Single();
        var run = await _repo.CreateRunAsync(new DuplicateScanOptions { SessionId = session.Id });
        await _repo.SaveResultsAsync(
            run.Id,
            [],
            [
                new DuplicateGroup
                {
                    Id = 1,
                    RunId = run.Id,
                    Method = DuplicateMethod.ExactSha256,
                    Algorithm = "SHA-256",
                    Confidence = 1.0,
                    TotalBytes = file.SizeBytes,
                    ReclaimableBytes = 0,
                    RepresentativeFileEntryId = file.Id,
                },
            ],
            [
                new DuplicateGroupMember
                {
                    Id = 1,
                    GroupId = 1,
                    FileEntryId = file.Id,
                    FullPath = file.FullPath,
                    FileName = file.FileName,
                    SizeBytes = file.SizeBytes,
                    ModifiedUtc = file.ModifiedUtc,
                    Attributes = file.Attributes,
                    Identity = file.Identity,
                    Score = 1.0,
                    IsKeeper = true,
                    IsSelected = false,
                    RecommendationReason = "Keeper",
                    ExistsNow = true,
                },
            ],
            []);
        var group = (await _repo.GetGroupsForRunAsync(run.Id)).Single();
        var member = (await _repo.GetMembersForGroupAsync(group.Id)).Single();

        var record = await _repo.RecordQuarantineAsync(member.Id, run.Id, @"C:\orig.txt", @"C:\q\orig.txt");

        var loaded = await _repo.GetQuarantinedFileAsync(record.Id);

        loaded.Should().NotBeNull();
        loaded!.OriginalPath.Should().Be(@"C:\orig.txt");
        loaded.QuarantinePath.Should().Be(@"C:\q\orig.txt");
        AssertUtcTimestamp(loaded.QuarantinedUtc, record.QuarantinedUtc);
        AssertUtcTimestamp(member.ModifiedUtc, file.ModifiedUtc);
        member.Attributes.Should().Be(file.Attributes);
        member.Identity.Should().Be(file.Identity);
    }

    [Fact]
    public async Task GenericCleanupQuarantine_RoundTripsWithNullMemberId()
    {
        // Schema v9: generic-cleanup quarantines have no duplicate group member.
        var record = await _repo.RecordQuarantineAsync(
            memberId: null,
            IQuarantineRecorder.GenericCleanupRunId,
            @"C:\temp\junk.log",
            @"C:\q\0\C_temp_junk.log");

        var loaded = await _repo.GetQuarantinedFileAsync(record.Id);

        loaded.Should().NotBeNull();
        loaded!.MemberId.Should().BeNull();
        loaded.RunId.Should().Be(IQuarantineRecorder.GenericCleanupRunId);
        loaded.OriginalPath.Should().Be(@"C:\temp\junk.log");
    }

    [Fact]
    public async Task GetUnrestoredQuarantinedFiles_ExcludesRestored_AndSpansAllRuns()
    {
        var generic = await _repo.RecordQuarantineAsync(null, 0, @"C:\a.log", @"C:\q\0\a.log");
        var restored = await _repo.RecordQuarantineAsync(null, 0, @"C:\b.log", @"C:\q\0\b.log");
        await _repo.MarkRestoredAsync(restored.Id, @"C:\b.log");

        var unrestored = await _repo.GetUnrestoredQuarantinedFilesAsync();

        unrestored.Select(static q => q.Id).Should().Contain(generic.Id);
        unrestored.Select(static q => q.Id).Should().NotContain(restored.Id,
            "restored files must disappear from the restorable list");
    }

    [Fact]
    public async Task RecoveryJournal_RoundTripsIntentAndOutcome()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        var run = await _repo.CreateRunAsync(new DuplicateScanOptions { SessionId = session.Id });
        var plannedUtc = new DateTime(2025, 8, 17, 12, 34, 56, DateTimeKind.Utc).AddTicks(7_654_321);
        var sourceModifiedUtc = plannedUtc.AddMinutes(-5);

        var planned = await _repo.RecordDuplicateOperationIntentAsync(new DuplicateOperationJournalEntry
        {
            OperationId = Guid.NewGuid(),
            Kind = DuplicateOperationKind.Delete,
            Status = DuplicateOperationStatus.Planned,
            RunId = run.Id,
            GroupId = 42,
            MemberId = 99,
            Method = DeletionMethod.Quarantine,
            SourcePath = @"C:\scope\dupe.bin",
            SourceIdentity = "volume:file:index",
            SourceSizeBytes = 1234,
            SourceModifiedUtc = sourceModifiedUtc,
            PlannedUtc = plannedUtc,
            MetadataJson = "{\"reason\":\"test\"}",
        });

        await _repo.UpdateDuplicateOperationOutcomeAsync(
            planned.Id,
            DuplicateOperationStatus.Quarantined,
            @"C:\quarantine\dupe.bin",
            1234,
            null);

        var entries = await _repo.GetDuplicateOperationJournalAsync(run.Id);

        entries.Should().ContainSingle();
        entries[0].Id.Should().Be(planned.Id);
        entries[0].Status.Should().Be(DuplicateOperationStatus.Quarantined);
        entries[0].DestinationPath.Should().Be(@"C:\quarantine\dupe.bin");
        entries[0].BytesFreed.Should().Be(1234);
        entries[0].CompletedUtc.Should().NotBeNull();
        entries[0].MetadataJson.Should().Contain("test");
        AssertUtcTimestamp(entries[0].SourceModifiedUtc!.Value, sourceModifiedUtc);
        AssertUtcTimestamp(entries[0].PlannedUtc, plannedUtc);
        entries[0].CompletedUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task CompleteQuarantineMove_AtomicallyTerminalizesJournalAndCreatesIdempotentRestoreRecord()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        var run = await _repo.CreateRunAsync(new DuplicateScanOptions { SessionId = session.Id });
        var planned = await _repo.RecordDuplicateOperationIntentAsync(new DuplicateOperationJournalEntry
        {
            OperationId = Guid.NewGuid(),
            Kind = DuplicateOperationKind.Delete,
            Status = DuplicateOperationStatus.Planned,
            RunId = run.Id,
            Method = DeletionMethod.Quarantine,
            SourcePath = @"C:\scope\duplicate.bin",
            SourceSizeBytes = 4096,
            PlannedUtc = DateTime.UtcNow,
        });

        var first = await _repo.CompleteQuarantineMoveAsync(
            planned.Id,
            memberId: null,
            run.Id,
            @"C:\scope\duplicate.bin",
            @"C:\quarantine\duplicate.bin",
            4096);
        var second = await _repo.CompleteQuarantineMoveAsync(
            planned.Id,
            memberId: null,
            run.Id,
            @"C:\scope\duplicate.bin",
            @"C:\quarantine\duplicate.bin",
            4096);

        second.Id.Should().Be(first.Id, "retries must not create duplicate restore records");
        var quarantined = await _repo.GetQuarantinedFilesAsync(run.Id);
        quarantined.Should().ContainSingle();
        quarantined[0].OriginalPath.Should().Be(@"C:\scope\duplicate.bin");
        quarantined[0].QuarantinePath.Should().Be(@"C:\quarantine\duplicate.bin");

        var journal = await _repo.GetDuplicateOperationJournalAsync(run.Id);
        journal.Should().ContainSingle();
        journal[0].Status.Should().Be(DuplicateOperationStatus.Quarantined);
        journal[0].DestinationPath.Should().Be(@"C:\quarantine\duplicate.bin");
        journal[0].BytesFreed.Should().Be(4096);
        journal[0].CompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task PagedGroupAndErrorQueries_ReturnExpectedSlices()
    {
        var occurredUtc = new DateTime(2025, 8, 17, 12, 34, 56, DateTimeKind.Utc).AddTicks(7_654_321);
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        await _scanRepository.InsertFileEntriesAsync([
            MakeEntry(session.Id, @"C:\scope\a1.bin", 100, FileTypeCategory.Unknown),
            MakeEntry(session.Id, @"C:\scope\a2.bin", 100, FileTypeCategory.Unknown),
            MakeEntry(session.Id, @"C:\scope\b1.bin", 90, FileTypeCategory.Unknown),
            MakeEntry(session.Id, @"C:\scope\b2.bin", 90, FileTypeCategory.Unknown),
        ]);
        var files = await _scanRepository.GetLargestFilesAsync(session.Id, 10);
        var fileList = files.ToList();
        var run = await _repo.CreateRunAsync(new DuplicateScanOptions { SessionId = session.Id });

        await _repo.SaveResultsAsync(
            run.Id,
            [],
            [
                new DuplicateGroup
                {
                    Id = 1, RunId = run.Id, Method = DuplicateMethod.ExactSha256, Algorithm = "SHA-256",
                    Confidence = 1.0, TotalBytes = 200, ReclaimableBytes = 100, RepresentativeFileEntryId = fileList[0].Id,
                },
                new DuplicateGroup
                {
                    Id = 2, RunId = run.Id, Method = DuplicateMethod.NormalizedText, Algorithm = "TEXT-NORM",
                    Confidence = 0.8, TotalBytes = 180, ReclaimableBytes = 90, RepresentativeFileEntryId = fileList[2].Id,
                },
            ],
            [
                new DuplicateGroupMember
                {
                    Id = 1, GroupId = 1, FileEntryId = fileList[0].Id, FullPath = fileList[0].FullPath, FileName = fileList[0].FileName,
                    SizeBytes = fileList[0].SizeBytes, ModifiedUtc = fileList[0].ModifiedUtc, Attributes = fileList[0].Attributes, Identity = fileList[0].Identity, Score = 1.0, IsKeeper = true, IsSelected = false, RecommendationReason = "keeper", ExistsNow = true,
                },
                new DuplicateGroupMember
                {
                    Id = 2, GroupId = 1, FileEntryId = fileList[1].Id, FullPath = fileList[1].FullPath, FileName = fileList[1].FileName,
                    SizeBytes = fileList[1].SizeBytes, ModifiedUtc = fileList[1].ModifiedUtc, Attributes = fileList[1].Attributes, Identity = fileList[1].Identity, Score = 1.0, IsKeeper = false, IsSelected = true, RecommendationReason = "duplicate", ExistsNow = true,
                },
                new DuplicateGroupMember
                {
                    Id = 3, GroupId = 2, FileEntryId = fileList[2].Id, FullPath = fileList[2].FullPath, FileName = fileList[2].FileName,
                    SizeBytes = fileList[2].SizeBytes, ModifiedUtc = fileList[2].ModifiedUtc, Attributes = fileList[2].Attributes, Identity = fileList[2].Identity, Score = 0.8, IsKeeper = true, IsSelected = false, RecommendationReason = "keeper", ExistsNow = true,
                },
                new DuplicateGroupMember
                {
                    Id = 4, GroupId = 2, FileEntryId = fileList[3].Id, FullPath = fileList[3].FullPath, FileName = fileList[3].FileName,
                    SizeBytes = fileList[3].SizeBytes, ModifiedUtc = fileList[3].ModifiedUtc, Attributes = fileList[3].Attributes, Identity = fileList[3].Identity, Score = 0.8, IsKeeper = false, IsSelected = false, RecommendationReason = "review", ExistsNow = false,
                },
            ],
            [
                new DuplicateError
                {
                    Id = 0, RunId = run.Id, FileEntryId = fileList[3].Id, Path = fileList[3].FullPath,
                    ErrorType = "DecoderError", Message = "test", OccurredUtc = occurredUtc,
                },
            ]);

        var summary = await _repo.GetDuplicateRunSummaryAsync(run.Id);
        summary.GroupCount.Should().Be(2);
        summary.ExactGroupCount.Should().Be(1);
        summary.ReviewGroupCount.Should().Be(1);
        summary.ErrorCount.Should().Be(1);

        var firstPage = await _repo.GetDuplicateGroupsPageAsync(
            run.Id, 1, 1, null, DuplicateGroupSortBy.ReclaimableBytesDesc);
        firstPage.Should().HaveCount(1);
        firstPage[0].Method.Should().Be(DuplicateMethod.ExactSha256);

        var filtered = await _repo.GetDuplicateGroupsPageAsync(
            run.Id, 1, 10, new DuplicateGroupQueryFilter { IncludeErroredOnly = true }, DuplicateGroupSortBy.ReclaimableBytesDesc);
        filtered.Should().ContainSingle();
        filtered[0].Method.Should().Be(DuplicateMethod.NormalizedText);

        var errorsPage = await _repo.GetDuplicateErrorsPageAsync(run.Id, 1, 10);
        errorsPage.Should().ContainSingle();
        AssertUtcTimestamp(errorsPage[0].OccurredUtc, occurredUtc);
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static FileEntry MakeEntry(
        long sessionId,
        string path,
        long size,
        FileTypeCategory category,
        FileAttributes attributes = FileAttributes.Normal) => new()
        {
            Id = 0,
            SessionId = sessionId,
            FullPath = path,
            FileName = Path.GetFileName(path),
            Extension = Path.GetExtension(path),
            SizeBytes = size,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow,
            AccessedUtc = DateTime.UtcNow,
            Attributes = attributes,
            Category = category,
            Identity = new FileIdentity("TESTVOL", 1),
            IsReparsePoint = false,
        };

    private static DuplicateSignature MakeSignature(FileEntry file, string hash, int revision) => new()
    {
        Id = 0,
        SessionId = file.SessionId,
        FileEntryId = file.Id,
        Method = DuplicateMethod.ExactSha256,
        Algorithm = "SHA-256",
        AlgorithmVersion = 1,
        SignatureText = hash,
        MetadataJson = $"{{\"revision\":{revision}}}",
        ComputedUtc = DateTime.UtcNow.AddMinutes(revision),
        Status = "Ready",
        SourceSizeBytes = file.SizeBytes,
        SourceModifiedUtc = file.ModifiedUtc,
    };

    private static void AssertUtcTimestamp(DateTime actual, DateTime expected)
    {
        actual.Kind.Should().Be(DateTimeKind.Utc);
        actual.Ticks.Should().Be(expected.Ticks);
    }
}
