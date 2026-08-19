using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;
using StorageMaster.Storage.Schema;

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
        await using var conn = await _ctx.GetConnectionAsync();

        // Assert: current version should be stamped through the latest additive migration.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
        var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        version.Should().Be(DatabaseSchema.CurrentVersion, "all migrations should stamp their versions");

        // Verify there are exactly 12 version rows (one per migration).
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(DatabaseSchema.CurrentVersion, "each migration level stamps its own row");

        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('CleanupLog') WHERE name = 'AuditDataJson';";
        var auditColumnCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        auditColumnCount.Should().Be(1, "the cleanup audit metadata column should exist after v4");

        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DriveHealthSnapshots';";
        var driveHealthTableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        driveHealthTableCount.Should().Be(1, "drive health snapshots should exist after v7");

        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DuplicateOperationJournal';";
        var journalTableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        journalTableCount.Should().Be(1, "duplicate operation recovery journal should exist after v8");

        cmd.CommandText = "SELECT [notnull] FROM pragma_table_info('QuarantinedFiles') WHERE name = 'MemberId';";
        var memberIdNotNull = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        memberIdNotNull.Should().Be(0, "QuarantinedFiles.MemberId must be nullable after v9");

        cmd.CommandText = "SELECT [on_delete] FROM pragma_foreign_key_list('QuarantinedFiles') WHERE [from] = 'MemberId';";
        var memberDeleteAction = Convert.ToString(await cmd.ExecuteScalarAsync());
        memberDeleteAction.Should().Be("SET NULL", "quarantine restore records must outlive deleted duplicate history after v10");
    }

    [Fact]
    public async Task SecondOpen_DoesNotReRunMigrations()
    {
        // First open — runs migrations.
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using (var initializationConnection = await _ctx.GetConnectionAsync())
        {
        }
        await _ctx.DisposeAsync();

        // Second open — should skip migrations.
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using var conn = await _ctx.GetConnectionAsync();

        // Should still have exactly 12 version rows (not 24).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SchemaVersion;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(DatabaseSchema.CurrentVersion, "migrations must not re-run on second open");
    }

    [Fact]
    public async Task Migration_RechecksVersionAfterWaitingForCompetingSqliteWriter()
    {
        // Start from a valid v13 database, then make its committed version look
        // like v12. The schema itself intentionally remains v13 so a competing
        // migrator only needs to stamp the version to model finishing first.
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using (var initialConnection = await _ctx.GetConnectionAsync())
        {
        }
        await _ctx.DisposeAsync();
        _ctx = null;
        SqliteConnection.ClearAllPools();

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            DefaultTimeout = 5,
        }.ToString();
        await using var competingConnection = new SqliteConnection(connectionString);
        await competingConnection.OpenAsync();

        using (var downgradeCommand = competingConnection.CreateCommand())
        {
            downgradeCommand.CommandText =
                $"DELETE FROM SchemaVersion WHERE Version = {DatabaseSchema.CurrentVersion};";
            (await downgradeCommand.ExecuteNonQueryAsync()).Should().Be(1);
        }

        // This immediate transaction is the SQLite boundary shared by separate
        // processes. The context can still read committed v10 state in WAL mode,
        // but must wait here before applying any migration DDL.
        using var competingTransaction = competingConnection.BeginTransaction(deferred: false);
        var logger = new MigrationStartLogger();
        _ctx = new StorageDbContext(_dbPath, logger);
        using var initializationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initialization = Task.Run(() => _ctx.GetConnectionAsync(initializationTimeout.Token));

        await logger.MigrationStarted.WaitAsync(TimeSpan.FromSeconds(5));

        using (var stampCommand = competingConnection.CreateCommand())
        {
            stampCommand.Transaction = competingTransaction;
            stampCommand.CommandText =
                $"INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES ({DatabaseSchema.CurrentVersion}, $appliedUtc);";
            stampCommand.Parameters.AddWithValue("$appliedUtc", DateTime.UtcNow.ToString("O"));
            await stampCommand.ExecuteNonQueryAsync();
        }
        await competingTransaction.CommitAsync();

        await using var initializedConnection =
            await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        using var countCommand = initializedConnection.CreateCommand();
        countCommand.CommandText =
            $"SELECT COUNT(*) FROM SchemaVersion WHERE Version = {DatabaseSchema.CurrentVersion};";
        Convert.ToInt32(await countCommand.ExecuteScalarAsync()).Should().Be(1,
            "a waiter must re-read the version after acquiring SQLite's writer reservation");
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null)
            await _ctx.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private sealed class MigrationStartLogger : ILogger<StorageDbContext>
    {
        private readonly TaskCompletionSource<bool> _migrationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task MigrationStarted => _migrationStarted.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information
                && formatter(state, exception).StartsWith("Migrating schema", StringComparison.Ordinal))
            {
                _migrationStarted.TrySetResult(true);
            }
        }
    }
}
