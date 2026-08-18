using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

public sealed class DriveHealthRepositoryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly DriveHealthRepository _repo;

    public DriveHealthRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"drivehealth_{Guid.NewGuid():N}.db");
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _repo = new DriveHealthRepository(_ctx);
    }

    [Fact]
    public async Task SaveSnapshotsAsync_PersistsLatestSnapshotPerDrive()
    {
        var capturedUtc = new DateTime(2025, 8, 17, 12, 34, 56, DateTimeKind.Utc).AddTicks(7_654_321);
        var older = Snapshot(@"C:\", DriveHealthStatus.Healthy, capturedUtc.AddMinutes(-5));
        var newer = Snapshot(@"C:\", DriveHealthStatus.Warning, capturedUtc);
        var other = Snapshot(@"D:\", DriveHealthStatus.Unsupported, capturedUtc.AddMinutes(-1));

        await _repo.SaveSnapshotsAsync([older, newer, other]);

        var latest = await _repo.GetLatestSnapshotsAsync();

        latest.Should().HaveCount(2);
        var latestSystem = latest.Single(static s => s.DriveName == @"C:\");
        latestSystem.Status.Should().Be(DriveHealthStatus.Warning);
        AssertUtcTimestamp(latestSystem.CapturedUtc, capturedUtc);
        latest.Single(static s => s.DriveName == @"D:\").Status.Should().Be(DriveHealthStatus.Unsupported);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsNewestFirstAndHonorsLimit()
    {
        await _repo.SaveSnapshotsAsync([
            Snapshot(@"C:\", DriveHealthStatus.Healthy, DateTime.UtcNow.AddMinutes(-3)),
            Snapshot(@"C:\", DriveHealthStatus.Warning, DateTime.UtcNow.AddMinutes(-2)),
            Snapshot(@"C:\", DriveHealthStatus.Critical, DateTime.UtcNow.AddMinutes(-1)),
        ]);

        var history = await _repo.GetHistoryAsync(@"C:\", limit: 2);

        history.Select(static s => s.Status).Should().Equal(DriveHealthStatus.Critical, DriveHealthStatus.Warning);
    }

    private static DriveHealthSnapshot Snapshot(string drive, DriveHealthStatus status, DateTime capturedUtc) => new()
    {
        DriveName = drive,
        VolumeLabel = "System",
        DriveFormat = "NTFS",
        TotalBytes = 1000,
        FreeBytes = 250,
        FreePercent = 25,
        Status = status,
        Source = "test",
        Message = "test snapshot",
        CapturedUtc = capturedUtc,
    };

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileNameWithoutExtension(_dbPath) + "*"))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void AssertUtcTimestamp(DateTime actual, DateTime expected)
    {
        actual.Kind.Should().Be(DateTimeKind.Utc);
        actual.Ticks.Should().Be(expected.Ticks);
    }
}
