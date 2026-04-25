using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// Integration tests that run against a real SQLite database in a temp file.
/// These tests verify schema creation, migration, and CRUD correctness.
/// </summary>
public sealed class ScanRepositoryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly ScanRepository _repo;

    public ScanRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _ctx    = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _repo   = new ScanRepository(_ctx);
    }

    [Fact]
    public async Task CreateSession_ReturnsSessionWithPositiveId()
    {
        var session = await _repo.CreateSessionAsync(@"C:\TestRoot");
        session.Id.Should().BeGreaterThan(0);
        session.Status.Should().Be(ScanStatus.Running);
        session.RootPath.Should().Be(@"C:\TestRoot");
    }

    [Fact]
    public async Task InsertAndQueryFileEntries_RoundTrip()
    {
        var session = await _repo.CreateSessionAsync(@"C:\");
        var entry   = new FileEntry
        {
            Id           = 0,
            SessionId    = session.Id,
            FullPath     = @"C:\test\large.iso",
            FileName     = "large.iso",
            Extension    = ".iso",
            SizeBytes    = 4_000_000_000L,
            CreatedUtc   = DateTime.UtcNow.AddDays(-10),
            ModifiedUtc  = DateTime.UtcNow.AddDays(-5),
            AccessedUtc  = DateTime.UtcNow,
            Attributes   = FileAttributes.Normal,
            Category     = FileTypeCategory.Archive,
        };

        await _repo.InsertFileEntriesAsync([entry]);

        var results = await _repo.GetLargestFilesAsync(session.Id, topN: 10);
        results.Should().ContainSingle();
        results[0].FileName.Should().Be("large.iso");
        results[0].SizeBytes.Should().Be(4_000_000_000L);
        results[0].Category.Should().Be(FileTypeCategory.Archive);
    }

    [Fact]
    public async Task UpdateSession_PersistsChanges()
    {
        var session   = await _repo.CreateSessionAsync(@"C:\");
        var completed = session with
        {
            Status        = ScanStatus.Completed,
            CompletedUtc  = DateTime.UtcNow,
            TotalFiles    = 12345,
            TotalSizeBytes = 5_000_000_000L,
        };

        await _repo.UpdateSessionAsync(completed);

        var loaded = await _repo.GetSessionAsync(session.Id);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(ScanStatus.Completed);
        loaded.TotalFiles.Should().Be(12345);
    }

    [Fact]
    public async Task GetCategoryBreakdown_AggregatesCorrectly()
    {
        var session = await _repo.CreateSessionAsync(@"C:\");
        await _repo.InsertFileEntriesAsync([
            MakeEntry(session.Id, ".mp4",  FileTypeCategory.Video,    1_000_000),
            MakeEntry(session.Id, ".mp4",  FileTypeCategory.Video,    2_000_000),
            MakeEntry(session.Id, ".docx", FileTypeCategory.Document,   500_000),
        ]);

        var breakdown = await _repo.GetCategoryBreakdownAsync(session.Id);

        breakdown.Should().ContainKey(FileTypeCategory.Video);
        breakdown[FileTypeCategory.Video].Count.Should().Be(2);
        breakdown[FileTypeCategory.Video].Bytes.Should().Be(3_000_000);
    }

    [Fact]
    public async Task UpsertFolderEntries_CreateAndAccumulate()
    {
        var session = await _repo.CreateSessionAsync(@"C:\");
        var folder = new FolderEntry
        {
            Id              = 0,
            SessionId       = session.Id,
            FullPath        = @"C:\TestFolder",
            FolderName      = "TestFolder",
            DirectSizeBytes = 1000,
            TotalSizeBytes  = 1000,
            FileCount       = 5,
            SubFolderCount  = 0,
            IsReparsePoint  = false,
            WasAccessDenied = false,
        };

        await _repo.UpsertFolderEntriesAsync([folder]);
        await _repo.UpsertFolderEntriesAsync([folder with { DirectSizeBytes = 500, TotalSizeBytes = 500, FileCount = 2 }]);

        var results = await _repo.GetLargestFoldersAsync(session.Id, topN: 10);
        results.Should().ContainSingle();
        results[0].DirectSizeBytes.Should().Be(1500, "upsert should accumulate direct bytes");
        results[0].FileCount.Should().Be(7);
    }

    [Fact]
    public async Task GetAllFolderPathsForSession_ReturnsAllUpsertedFolders()
    {
        var session = await _repo.CreateSessionAsync(@"C:\");
        await _repo.UpsertFolderEntriesAsync([
            MakeFolderEntry(session.Id, @"C:\FolderA", 1000),
            MakeFolderEntry(session.Id, @"C:\FolderB", 2000),
        ]);

        var results = await _repo.GetAllFolderPathsForSessionAsync(session.Id);

        results.Should().HaveCount(2);
        results.Select(f => f.FullPath).Should().Contain([@"C:\FolderA", @"C:\FolderB"]);
    }

    [Fact]
    public async Task UpdateFolderTotals_SetsTotalSizeBytes()
    {
        var session = await _repo.CreateSessionAsync(@"C:\");
        await _repo.UpsertFolderEntriesAsync([
            MakeFolderEntry(session.Id, @"C:\Root",       500),
            MakeFolderEntry(session.Id, @"C:\Root\Child", 300),
        ]);

        var totals = new Dictionary<string, long>
        {
            [@"C:\Root"]       = 800,
            [@"C:\Root\Child"] = 300,
        };
        await _repo.UpdateFolderTotalsAsync(session.Id, totals);

        var results = await _repo.GetAllFolderPathsForSessionAsync(session.Id);
        results.First(f => f.FullPath == @"C:\Root").TotalSizeBytes.Should().Be(800);
        results.First(f => f.FullPath == @"C:\Root\Child").TotalSizeBytes.Should().Be(300);
    }

    [Fact]
    public async Task DeleteSession_RemovesSessionAndCascades()
    {
        var session = await _repo.CreateSessionAsync(@"C:\");
        await _repo.InsertFileEntriesAsync([MakeEntry(session.Id, ".txt", FileTypeCategory.Document, 1024)]);

        await _repo.DeleteSessionAsync(session.Id);

        var loaded = await _repo.GetSessionAsync(session.Id);
        loaded.Should().BeNull("deleted session should not be found");

        var files = await _repo.GetLargestFilesAsync(session.Id, topN: 10);
        files.Should().BeEmpty("cascade delete should remove all file entries");
    }

    [Fact]
    public async Task GetRecentSessions_ReturnsMostRecentFirst()
    {
        await _repo.CreateSessionAsync(@"C:\first");
        await Task.Delay(10);
        await _repo.CreateSessionAsync(@"C:\second");

        var sessions = await _repo.GetRecentSessionsAsync(count: 10);

        sessions.Should().HaveCountGreaterThanOrEqualTo(2);
        sessions[0].RootPath.Should().Be(@"C:\second", "most recent session should come first");
    }

    [Fact]
    public async Task GetLargestFolders_ReturnsInDescendingOrder()
    {
        var session = await _repo.CreateSessionAsync(@"C:\");
        await _repo.UpsertFolderEntriesAsync([
            MakeFolderEntry(session.Id, @"C:\Small",  100),
            MakeFolderEntry(session.Id, @"C:\Large", 9000),
            MakeFolderEntry(session.Id, @"C:\Mid",   5000),
        ]);

        await _repo.UpdateFolderTotalsAsync(session.Id, new Dictionary<string, long>
        {
            [@"C:\Small"]  =  100,
            [@"C:\Large"]  = 9000,
            [@"C:\Mid"]    = 5000,
        });

        var results = await _repo.GetLargestFoldersAsync(session.Id, topN: 10);

        results[0].TotalSizeBytes.Should().BeGreaterThanOrEqualTo(results[1].TotalSizeBytes,
            "folders should be ordered by TotalSizeBytes descending");
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static FolderEntry MakeFolderEntry(long sessionId, string path, long directBytes) => new()
    {
        Id              = 0,
        SessionId       = sessionId,
        FullPath        = path,
        FolderName      = Path.GetFileName(path) ?? path,
        DirectSizeBytes = directBytes,
        TotalSizeBytes  = directBytes,
        FileCount       = 1,
        SubFolderCount  = 0,
        IsReparsePoint  = false,
        WasAccessDenied = false,
    };

    private static FileEntry MakeEntry(long sessionId, string ext, FileTypeCategory cat, long size) => new()
    {
        Id           = 0,
        SessionId    = sessionId,
        FullPath     = $@"C:\test\file{Guid.NewGuid():N}{ext}",
        FileName     = $"file{ext}",
        Extension    = ext,
        SizeBytes    = size,
        CreatedUtc   = DateTime.UtcNow,
        ModifiedUtc  = DateTime.UtcNow,
        AccessedUtc  = DateTime.UtcNow,
        Attributes   = FileAttributes.Normal,
        Category     = cat,
    };
}
