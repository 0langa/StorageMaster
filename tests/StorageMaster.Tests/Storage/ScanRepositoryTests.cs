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

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

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
