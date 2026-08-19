using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// DriveHealthSnapshots has no session foreign key, so purging scan history never
/// touched it and nothing else pruned it — the table grew for the lifetime of the
/// install. These tests pin the retention pass that now runs inside
/// <see cref="DriveHealthRepository.SaveSnapshotsAsync"/>: newest rows survive, other
/// drives are untouched, and the cap holds across separate saves.
/// </summary>
public sealed class DriveHealthRetentionTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly DriveHealthRepository _repo;

    public DriveHealthRetentionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"drivehealthretention_{Guid.NewGuid():N}.db");
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _repo = new DriveHealthRepository(_ctx);
    }

    [Fact]
    public async Task SaveSnapshotsAsync_KeepsNewestSnapshotsPerDriveAndLeavesOtherDrivesAlone()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int overflow = 5;

        var snapshots = new List<DriveHealthSnapshot>();
        for (var minute = 0; minute < DriveHealthRepository.MaxSnapshotsPerDrive + overflow; minute++)
            snapshots.Add(Snapshot(@"C:\", start.AddMinutes(minute)));
        snapshots.Add(Snapshot(@"D:\", start));
        snapshots.Add(Snapshot(@"D:\", start.AddMinutes(1)));

        await _repo.SaveSnapshotsAsync(snapshots);

        var kept = await _repo.GetHistoryAsync(@"C:\", limit: DriveHealthRepository.MaxSnapshotsPerDrive);
        kept.Should().HaveCount(DriveHealthRepository.MaxSnapshotsPerDrive);
        AssertUtcTimestamp(
            kept[0].CapturedUtc,
            start.AddMinutes(DriveHealthRepository.MaxSnapshotsPerDrive + overflow - 1));
        AssertUtcTimestamp(kept[^1].CapturedUtc, start.AddMinutes(overflow));

        // The drive that stayed under the cap must not lose anything.
        var other = await _repo.GetHistoryAsync(@"D:\");
        other.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveSnapshotsAsync_PrunesRowsWrittenByEarlierSaves()
    {
        var start = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var initial = new List<DriveHealthSnapshot>();
        for (var minute = 0; minute < DriveHealthRepository.MaxSnapshotsPerDrive; minute++)
            initial.Add(Snapshot(@"C:\", start.AddMinutes(minute)));
        await _repo.SaveSnapshotsAsync(initial);

        var newest = start.AddMinutes(DriveHealthRepository.MaxSnapshotsPerDrive);
        await _repo.SaveSnapshotsAsync([Snapshot(@"C:\", newest, DriveHealthStatus.Critical)]);

        var kept = await _repo.GetHistoryAsync(@"C:\", limit: DriveHealthRepository.MaxSnapshotsPerDrive);
        kept.Should().HaveCount(DriveHealthRepository.MaxSnapshotsPerDrive);
        kept[0].Status.Should().Be(DriveHealthStatus.Critical);
        AssertUtcTimestamp(kept[0].CapturedUtc, newest);
        AssertUtcTimestamp(kept[^1].CapturedUtc, start.AddMinutes(1));

        var latest = await _repo.GetLatestSnapshotsAsync();
        latest.Should().ContainSingle();
        AssertUtcTimestamp(latest[0].CapturedUtc, newest);
    }

    private static DriveHealthSnapshot Snapshot(
        string drive,
        DateTime capturedUtc,
        DriveHealthStatus status = DriveHealthStatus.Healthy) => new()
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
