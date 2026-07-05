using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Settings;

/// <summary>
/// Regression tests for the settings load-modify-save race: the low-disk
/// monitor, the scheduler, and the settings page all mutate AppSettings
/// concurrently. UpdateAsync must serialise those mutations so no writer
/// drops another writer's change.
/// </summary>
public sealed class SettingsUpdateRaceTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly SettingsRepository _repo;

    public SettingsUpdateRaceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_settings_race_{Guid.NewGuid():N}.db");
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _repo = new SettingsRepository(_ctx);
    }

    [Fact]
    public async Task ConcurrentUpdates_ToDifferentKeys_AllSurvive()
    {
        // 30 concurrent writers each stamp a distinct notification-state key —
        // the exact pattern MainWindow.CheckLowDiskAsync uses.
        var tasks = Enumerable.Range(0, 30)
            .Select(i => Task.Run(() => _repo.UpdateAsync(s =>
                s.LowDiskNotificationState[$"D{i}:|warning"] = $"stamp-{i}")))
            .ToArray();

        await Task.WhenAll(tasks);

        var final = await _repo.LoadAsync();
        final.LowDiskNotificationState.Should().HaveCount(30,
            "no concurrent update may drop another writer's key");
        for (int i = 0; i < 30; i++)
            final.LowDiskNotificationState[$"D{i}:|warning"].Should().Be($"stamp-{i}");
    }

    [Fact]
    public async Task UpdateAsync_InterleavedWithDistinctFields_KeepsBothChanges()
    {
        // Writer A mutates a scalar user preference; writer B mutates the
        // notification dictionary. Both changes must be present afterwards.
        var a = Task.Run(() => _repo.UpdateAsync(s => s.DefaultScanPath = @"C:\from-settings-page"));
        var b = Task.Run(() => _repo.UpdateAsync(s => s.DriveHealthNotificationState["C:|Warning"] = "seen"));

        await Task.WhenAll(a, b);

        var final = await _repo.LoadAsync();
        final.DefaultScanPath.Should().Be(@"C:\from-settings-page");
        final.DriveHealthNotificationState.Should().ContainKey("C:|Warning");
    }

    [Fact]
    public async Task UpdateAsync_ReturnsPersistedSettings()
    {
        var returned = await _repo.UpdateAsync(s => s.LargeFileSizeMb = 777);

        returned.LargeFileSizeMb.Should().Be(777);
        (await _repo.LoadAsync()).LargeFileSizeMb.Should().Be(777);
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
