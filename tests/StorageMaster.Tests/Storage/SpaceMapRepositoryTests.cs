using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Core.SpaceMap;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

public sealed class SpaceMapRepositoryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly ScanRepository _scanRepository;
    private readonly SpaceMapRepository _spaceMapRepository;

    public SpaceMapRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"spacemap_{Guid.NewGuid():N}.db");
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _scanRepository = new ScanRepository(_ctx);
        _spaceMapRepository = new SpaceMapRepository(_ctx);
    }

    [Fact]
    public async Task GetFolderChildrenWithSizes_ReturnsDirectChildrenOnly()
    {
        var modifiedUtc = new DateTime(2025, 8, 17, 12, 34, 56, DateTimeKind.Utc).AddTicks(7_654_321);
        var session = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.UpsertFolderEntriesAsync([
            MakeFolder(session.Id, @"C:\Root", 10_000),
            MakeFolder(session.Id, @"C:\Root\A", 6_000),
            MakeFolder(session.Id, @"C:\Root\A\Nested", 3_000),
            MakeFolder(session.Id, @"C:\Root\B", 2_000),
        ]);
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(session.Id, @"C:\Root\top.bin", 1_000) with { ModifiedUtc = modifiedUtc },
            MakeFile(session.Id, @"C:\Root\A\nested.bin", 2_000),
        ]);

        var children = await _spaceMapRepository.GetFolderChildrenWithSizesAsync(
            session.Id,
            @"C:\Root",
            kindFilter: null,
            minimumSizeBytes: 0,
            limit: 20);

        children.Select(static node => node.FullPath)
            .Should()
            .BeEquivalentTo([@"C:\Root\A", @"C:\Root\B", @"C:\Root\top.bin"]);
        children.Should().OnlyContain(static node => node.FullPath != @"C:\Root\A\Nested");
        AssertUtcTimestamp(
            children.Single(static node => node.FullPath == @"C:\Root\top.bin").ModifiedUtc!.Value,
            modifiedUtc);
    }

    [Fact]
    public async Task GetFolderChildrenWithSizes_AppliesLimitAcrossFoldersAndFiles()
    {
        var session = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.UpsertFolderEntriesAsync([
            MakeFolder(session.Id, @"C:\Root", 20_000),
            MakeFolder(session.Id, @"C:\Root\Small", 1_000),
            MakeFolder(session.Id, @"C:\Root\Medium", 2_000),
        ]);
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(session.Id, @"C:\Root\huge.bin", 10_000),
        ]);

        var children = await _spaceMapRepository.GetFolderChildrenWithSizesAsync(
            session.Id,
            @"C:\Root",
            kindFilter: null,
            minimumSizeBytes: 0,
            limit: 2);

        children.Select(static node => node.FullPath).Should().Equal([
            @"C:\Root\huge.bin",
            @"C:\Root\Medium",
        ]);
    }

    [Fact]
    public async Task SpaceMapPathQueries_TreatPercentAndUnderscoreAsLiteralPathCharacters()
    {
        var session = await CreateCompletedSessionAsync(@"C:\Scan");
        await _scanRepository.UpsertFolderEntriesAsync([
            MakeFolder(session.Id, @"C:\Scan", 20_000),
            MakeFolder(session.Id, @"C:\Scan\Bucket_%", 8_000),
            MakeFolder(session.Id, @"C:\Scan\Bucket_%\Child", 2_000),
            MakeFolder(session.Id, @"C:\Scan\BucketX\Wrong", 5_000),
        ]);
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(session.Id, @"C:\Scan\Bucket_%\literal.bin", 3_000),
            MakeFile(session.Id, @"C:\Scan\BucketX\wrong.bin", 4_000),
        ]);

        var children = await _spaceMapRepository.GetFolderChildrenWithSizesAsync(
            session.Id,
            @"C:\Scan\Bucket_%",
            kindFilter: null,
            minimumSizeBytes: 0,
            limit: 20);
        var largestFiles = await _spaceMapRepository.GetLargestFilesUnderFolderAsync(
            session.Id,
            @"C:\Scan\Bucket_%",
            limit: 20);

        children.Select(static node => node.FullPath).Should().BeEquivalentTo([
            @"C:\Scan\Bucket_%\Child",
            @"C:\Scan\Bucket_%\literal.bin",
        ]);
        largestFiles.Select(static node => node.FullPath).Should().Equal(
            @"C:\Scan\Bucket_%\literal.bin");
    }

    [Fact]
    public async Task GetPreviousComparableSession_ReturnsSameRootPreviousScan()
    {
        var previous = await CreateCompletedSessionAsync(@"C:\Root");
        await Task.Delay(10);
        var current = await CreateCompletedSessionAsync(@"C:\Root");
        await CreateCompletedSessionAsync(@"D:\Other");

        var comparable = await _spaceMapRepository.GetPreviousComparableSessionAsync(current.Id);

        comparable.Should().NotBeNull();
        comparable!.Id.Should().Be(previous.Id);
        AssertUtcTimestamp(comparable.StartedUtc, previous.StartedUtc);
        AssertUtcTimestamp(comparable.CompletedUtc!.Value, previous.CompletedUtc!.Value);
    }

    [Fact]
    public async Task GetScanDelta_ReturnsGrowthNewAndRemovedFiles()
    {
        var previous = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.UpsertFolderEntriesAsync([
            MakeFolder(previous.Id, @"C:\Root", 5_000),
            MakeFolder(previous.Id, @"C:\Root\A", 2_000),
        ]);
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(previous.Id, @"C:\Root\old.bin", 1_000),
            MakeFile(previous.Id, @"C:\Root\removed.bin", 3_000),
        ]);

        await Task.Delay(10);
        var current = await CreateCompletedSessionAsync(@"C:\Root");
        await _scanRepository.UpsertFolderEntriesAsync([
            MakeFolder(current.Id, @"C:\Root", 10_000),
            MakeFolder(current.Id, @"C:\Root\A", 7_000),
        ]);
        await _scanRepository.InsertFileEntriesAsync([
            MakeFile(current.Id, @"C:\Root\old.bin", 1_500),
            MakeFile(current.Id, @"C:\Root\new.bin", 4_000),
        ]);

        var delta = await _spaceMapRepository.GetScanDeltaAsync(current.Id, previous.Id, 10);

        delta.GrowingFolders.Should().Contain(static item => item.FullPath == @"C:\Root\A" && item.DeltaBytes == 5_000);
        delta.NewLargeFiles.Should().Contain(static item => item.FullPath == @"C:\Root\new.bin");
        delta.RemovedFiles.Should().Contain(static item => item.FullPath == @"C:\Root\removed.bin");
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
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

    private static FolderEntry MakeFolder(long sessionId, string path, long totalBytes) => new()
    {
        Id = 0,
        SessionId = sessionId,
        FullPath = path,
        FolderName = Path.GetFileName(path),
        DirectSizeBytes = totalBytes,
        TotalSizeBytes = totalBytes,
        FileCount = 1,
        SubFolderCount = 0,
        IsReparsePoint = false,
        WasAccessDenied = false,
    };

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

    private static void AssertUtcTimestamp(DateTime actual, DateTime expected)
    {
        actual.Kind.Should().Be(DateTimeKind.Utc);
        actual.Ticks.Should().Be(expected.Ticks);
    }
}
