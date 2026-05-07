using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// C5: Verifies that schema migrations stamp the version atomically with the DDL.
/// After successful migration, the SchemaVersion table contains the expected version.
/// </summary>
public sealed class AtomicMigrationTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private StorageDbContext? _ctx;

    public AtomicMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_c5_{Guid.NewGuid():N}.db");
    }

    [Fact]
    public async Task Migration_StampsVersionInsideTransaction()
    {
        // Act: create context → triggers migration.
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        var conn = await _ctx.GetConnectionAsync();

        // Assert: current version should be stamped through the latest additive migration.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
        var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        version.Should().Be(7, "all migrations should stamp their versions");

        // Verify there are exactly 7 version rows (one per migration).
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(7, "each migration level stamps its own row");

        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('CleanupLog') WHERE name = 'AuditDataJson';";
        var auditColumnCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        auditColumnCount.Should().Be(1, "the cleanup audit metadata column should exist after v4");

        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DriveHealthSnapshots';";
        var driveHealthTableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        driveHealthTableCount.Should().Be(1, "drive health snapshots should exist after v7");
    }

    [Fact]
    public async Task SecondOpen_DoesNotReRunMigrations()
    {
        // First open — runs migrations.
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await _ctx.GetConnectionAsync();
        await _ctx.DisposeAsync();

        // Second open — should skip migrations.
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        var conn = await _ctx.GetConnectionAsync();

        // Should still have exactly 7 version rows (not 14).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(7, "migrations must not re-run on second open");
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null)
            await _ctx.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
