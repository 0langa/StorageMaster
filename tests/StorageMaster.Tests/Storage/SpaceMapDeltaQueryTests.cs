using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// Guards the added/removed halves of <see cref="SpaceMapRepository.GetScanDeltaAsync"/>.
/// Those two queries were rewritten from a size-ordered LEFT JOIN into an id-only
/// anti-join subquery so SQLite can drive them from the covering index on
/// (SessionId, NormalizedFullPath); these tests pin the behaviour that rewrite must
/// preserve — membership, size ranking, the limit, and case-insensitive path identity.
/// </summary>
public sealed class SpaceMapDeltaQueryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly ScanRepository _scanRepository;
    private readonly SpaceMapRepository _spaceMapRepository;

    public SpaceMapDeltaQueryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"spacemapdelta_{Guid.NewGuid():N}.db");
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _scanRepository = new ScanRepository(_ctx);
        _spaceMapRepository = new SpaceMapRepository(_ctx);
    }

    [Fact]
    public async Task GetScanDelta_RanksAddedAndRemovedFilesBySizeAndHonorsLimit()
    {
        var previous = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(previous.Id, @"C:\Root\kept.bin", 8_000),
            MakeFile(previous.Id, @"C:\Root\gone-small.bin", 3_000),
            MakeFile(previous.Id, @"C:\Root\gone-large.bin", 9_000),
        ]);

        var current = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(current.Id, @"C:\Root\kept.bin", 8_500),
            MakeFile(current.Id, @"C:\Root\added-small.bin", 1_000),
            MakeFile(current.Id, @"C:\Root\added-medium.bin", 5_000),
            MakeFile(current.Id, @"C:\Root\added-large.bin", 7_000),
        ]);

        var full = await _spaceMapRepository.GetScanDeltaAsync(current.Id, previous.Id, limit: 10);

        full.NewLargeFiles.Select(static item => item.FullPath).Should().Equal(
            @"C:\Root\added-large.bin",
            @"C:\Root\added-medium.bin",
            @"C:\Root\added-small.bin");
        full.RemovedFiles.Select(static item => item.FullPath).Should().Equal(
            @"C:\Root\gone-large.bin",
            @"C:\Root\gone-small.bin");

        // A file present in both sessions is neither added nor removed, even though
        // its size changed between them.
        full.NewLargeFiles.Should().NotContain(static item => item.FullPath == @"C:\Root\kept.bin");
        full.RemovedFiles.Should().NotContain(static item => item.FullPath == @"C:\Root\kept.bin");

        var limited = await _spaceMapRepository.GetScanDeltaAsync(current.Id, previous.Id, limit: 2);

        limited.NewLargeFiles.Select(static item => item.FullPath).Should().Equal(
            @"C:\Root\added-large.bin",
            @"C:\Root\added-medium.bin");
        limited.RemovedFiles.Select(static item => item.FullPath).Should().Equal(
            @"C:\Root\gone-large.bin",
            @"C:\Root\gone-small.bin");
    }

    [Fact]
    public async Task GetScanDelta_MatchesPathsThatDifferOnlyInCase()
    {
        var previous = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(previous.Id, @"C:\Root\Mixed Case.bin", 4_000),
        ]);

        var current = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(current.Id, @"C:\ROOT\MIXED CASE.BIN", 4_000),
        ]);

        var delta = await _spaceMapRepository.GetScanDeltaAsync(current.Id, previous.Id, limit: 10);

        // Identity is NormalizedFullPath, not FullPath: a re-cased path is the same
        // file, so it must not show up as both added and removed.
        delta.NewLargeFiles.Should().BeEmpty();
        delta.RemovedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task GetScanDelta_WithNoFileChanges_ReturnsEmptyAddedAndRemovedLists()
    {
        var previous = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(previous.Id, @"C:\Root\a.bin", 1_000),
            MakeFile(previous.Id, @"C:\Root\b.bin", 2_000),
        ]);

        var current = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(current.Id, @"C:\Root\a.bin", 1_000),
            MakeFile(current.Id, @"C:\Root\b.bin", 2_000),
        ]);

        var delta = await _spaceMapRepository.GetScanDeltaAsync(current.Id, previous.Id, limit: 10);

        delta.NewLargeFiles.Should().BeEmpty();
        delta.RemovedFiles.Should().BeEmpty();
        delta.CurrentSessionId.Should().Be(current.Id);
        delta.PreviousSessionId.Should().Be(previous.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileNameWithoutExtension(_dbPath) + "*"))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private async Task<ScanSession> CreateCompletedSessionAsync(string root)
    {
        var session = await _scanRepository.CreateSessionAsync(root);
        var completed = session with
        {
            Status = ScanStatus.Completed,
            CompletedUtc = DateTime.UtcNow,
            TotalSizeBytes = 10_000,
            TotalFiles = 10,
            TotalFolders = 3,
        };
        await _scanRepository.UpdateSessionAsync(completed);
        return completed;
    }

    private static FileEntry MakeFile(long sessionId, string path, long sizeBytes) => new()
    {
        Id = 0,
        SessionId = sessionId,
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        SizeBytes = sizeBytes,
        CreatedUtc = DateTime.UtcNow,
        ModifiedUtc = DateTime.UtcNow,
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Unknown,
    };
}
