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
