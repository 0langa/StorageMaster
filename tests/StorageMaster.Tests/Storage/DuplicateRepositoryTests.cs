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
        await _scanRepository.InsertFileEntriesAsync([
            MakeEntry(session.Id, @"C:\scope\a.txt", 10, FileTypeCategory.Document),
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
    public async Task SaveResultsAsync_UpsertsExistingSignature()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\scope");
        await _scanRepository.InsertFileEntriesAsync([MakeEntry(session.Id, @"C:\scope\a.bin", 512, FileTypeCategory.Unknown)]);
        var file = (await _scanRepository.GetLargestFilesAsync(session.Id, 1)).Single();

        var run1 = await _repo.CreateRunAsync(new DuplicateScanOptions { SessionId = session.Id });
        var run2 = await _repo.CreateRunAsync(new DuplicateScanOptions { SessionId = session.Id });

        await _repo.SaveResultsAsync(run1.Id, [MakeSignature(file, "hash-a", 1)], [], [], []);
        await _repo.SaveResultsAsync(run2.Id, [MakeSignature(file, "hash-b", 2)], [], [], []);

        var cached = await _repo.GetCachedSignaturesAsync(session.Id, DuplicateMethod.ExactSha256, "SHA-256", 1);

        cached.Should().ContainSingle();
        cached[0].SignatureText.Should().Be("hash-b");
        cached[0].MetadataJson.Should().Contain("\"revision\":2");
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
    }

    [Fact]
    public async Task PagedGroupAndErrorQueries_ReturnExpectedSlices()
    {
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
                    SizeBytes = fileList[0].SizeBytes, ModifiedUtc = fileList[0].ModifiedUtc, Score = 1.0, IsKeeper = true, IsSelected = false, RecommendationReason = "keeper", ExistsNow = true,
                },
                new DuplicateGroupMember
                {
                    Id = 2, GroupId = 1, FileEntryId = fileList[1].Id, FullPath = fileList[1].FullPath, FileName = fileList[1].FileName,
                    SizeBytes = fileList[1].SizeBytes, ModifiedUtc = fileList[1].ModifiedUtc, Score = 1.0, IsKeeper = false, IsSelected = true, RecommendationReason = "duplicate", ExistsNow = true,
                },
                new DuplicateGroupMember
                {
                    Id = 3, GroupId = 2, FileEntryId = fileList[2].Id, FullPath = fileList[2].FullPath, FileName = fileList[2].FileName,
                    SizeBytes = fileList[2].SizeBytes, ModifiedUtc = fileList[2].ModifiedUtc, Score = 0.8, IsKeeper = true, IsSelected = false, RecommendationReason = "keeper", ExistsNow = true,
                },
                new DuplicateGroupMember
                {
                    Id = 4, GroupId = 2, FileEntryId = fileList[3].Id, FullPath = fileList[3].FullPath, FileName = fileList[3].FileName,
                    SizeBytes = fileList[3].SizeBytes, ModifiedUtc = fileList[3].ModifiedUtc, Score = 0.8, IsKeeper = false, IsSelected = false, RecommendationReason = "review", ExistsNow = false,
                },
            ],
            [
                new DuplicateError
                {
                    Id = 0, RunId = run.Id, FileEntryId = fileList[3].Id, Path = fileList[3].FullPath,
                    ErrorType = "DecoderError", Message = "test", OccurredUtc = DateTime.UtcNow,
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
}
