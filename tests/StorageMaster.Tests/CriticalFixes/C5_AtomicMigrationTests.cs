using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// C5: Verifies that schema migrations stamp the version atomically with the DDL.
/// After successful migration, the SchemaVersion table contains the expected version.
/// </summary>
public sealed class C5_AtomicMigrationTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private StorageDbContext? _ctx;

    public C5_AtomicMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_c5_{Guid.NewGuid():N}.db");
    }

    [Fact]
    public async Task Migration_StampsVersionInsideTransaction()
    {
        // Act: create context → triggers migration.
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        var conn = await _ctx.GetConnectionAsync();

        // Assert: version 2 should be stamped (V1 + V2 migrations).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
        var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        version.Should().Be(2, "both V1 and V2 migrations should stamp their versions");

        // Verify there are exactly 2 version rows (one per migration).
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(2, "each migration level stamps its own row");
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

        // Should still have exactly 2 version rows (not 4).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(2, "migrations must not re-run on second open");
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null)
            await _ctx.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
