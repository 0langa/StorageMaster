using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using StorageMaster.Storage.Schema;

namespace StorageMaster.Storage;

/// <summary>
/// Serializes schema initialization and creates configured pooled connection
/// leases. Every GetConnectionAsync caller owns the returned connection and
/// must dispose it asynchronously after that operation.
///
/// WAL mode lets independently leased readers and the serialized writer run
/// concurrently without sharing non-thread-safe Microsoft.Data.Sqlite objects.
/// </summary>
public sealed class StorageDbContext : IAsyncDisposable
{
    // A process may briefly create more than one context for the same file
    // (tests, service re-composition, or overlapping app lifetime). Coordinate
    // first-use migration and writes by canonical path so those contexts cannot
    // replay a migration or race SQLite's single-writer boundary. Entries are
    // intentionally process-lifetime: disposing a context must not invalidate
    // a semaphore still held by an in-flight operation from another context.
    private static readonly ConcurrentDictionary<string, DatabaseCoordination> DatabaseCoordinations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ILogger<StorageDbContext> _logger;
    private readonly DatabaseCoordination _coordination;
    private bool _initialized;
    private int _disposed;

    /// <summary>
    /// Serialises transactional writes across independently leased connections,
    /// keeping one SQLite writer at a time while WAL readers continue.
    /// </summary>
    public SemaphoreSlim WriteLock => _coordination.WriteLock;

    public StorageDbContext(string dbPath, ILogger<StorageDbContext> logger)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _logger = logger;
        _coordination = DatabaseCoordinations.GetOrAdd(
            _dbPath,
            static _ => new DatabaseCoordination());
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            // In-process writers use WriteLock. This timeout is the bounded
            // fallback for another process or an uncoordinated SQLite client.
            DefaultTimeout = 30,
        }.ToString();
    }

    /// <summary>
    /// Returns a new open, configured pooled connection. The caller owns the
    /// lease and must use <c>await using</c> to dispose it deterministically.
    /// </summary>
    public async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(ct);
        ThrowIfDisposed();

        var connection = await OpenConfiguredConnectionAsync(ct);
        if (Volatile.Read(ref _disposed) == 0)
            return connection;

        await connection.DisposeAsync();
        throw new ObjectDisposedException(nameof(StorageDbContext));
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _initialized))
            return;

        await _coordination.InitializationLock.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (_initialized)
                return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using var connection = await OpenRawConnectionAsync(ct);
            await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", ct);
            await MigrateAsync(connection, ct);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", ct);
            await ConfigureConnectionAsync(connection, ct);

            ThrowIfDisposed();
            Volatile.Write(ref _initialized, true);
            _logger.LogInformation("Database initialized: {Path}", _dbPath);
        }
        finally
        {
            _coordination.InitializationLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConfiguredConnectionAsync(CancellationToken ct)
    {
        var connection = await OpenRawConnectionAsync(ct);
        try
        {
            await ConfigureConnectionAsync(connection, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<SqliteConnection> OpenRawConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;", ct);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", ct);
        await ExecuteAsync(connection, "PRAGMA temp_store=MEMORY;", ct);
        await ExecuteAsync(connection, "PRAGMA cache_size=-32000;", ct); // 32 MB page cache
    }

    private async Task MigrateAsync(SqliteConnection conn, CancellationToken ct)
    {
        int current = await GetSchemaVersionAsync(conn, _logger, transaction: null, ct);
        ThrowIfSchemaVersionIsUnsupported(current);

        if (current >= DatabaseSchema.CurrentVersion)
            return;

        _logger.LogInformation("Migrating schema from v{Current} to v{Target}",
            current, DatabaseSchema.CurrentVersion);

        ct.ThrowIfCancellationRequested();

        // Acquire SQLite's cross-process writer reservation before trusting the
        // observed version. A different process may have completed migration
        // while this connection waited, so re-read inside the transaction and
        // apply only the still-missing levels. One transaction also keeps every
        // DDL change and version stamp atomic as a complete migration sequence.
        using var transaction = conn.BeginTransaction(deferred: false);
        current = await GetSchemaVersionAsync(conn, _logger, transaction, ct);
        ThrowIfSchemaVersionIsUnsupported(current);

        if (current < 1)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V1Statements, 1, ct);

        if (current < 2)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V2Statements, 2, ct);

        if (current < 3)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V3Statements, 3, ct);

        if (current < 4)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V4Statements, 4, ct);

        if (current < 5)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V5Statements, 5, ct);

        if (current < 6)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V6Statements, 6, ct);

        if (current < 7)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V7Statements, 7, ct);

        if (current < 8)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V8Statements, 8, ct);

        if (current < 9)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V9Statements, 9, ct);

        if (current < 10)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V10Statements, 10, ct);

        if (current < 11)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V11Statements, 11, ct);

        if (current < 12)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V12Statements, 12, ct);

        if (current < 13)
            await ApplyMigrationAsync(conn, transaction, DatabaseSchema.V13Statements, 13, ct);

        await transaction.CommitAsync(ct);
    }

    private static void ThrowIfSchemaVersionIsUnsupported(int current)
    {
        if (current > DatabaseSchema.CurrentVersion)
        {
            throw new NotSupportedException(
                $"Database schema version {current} is newer than supported version {DatabaseSchema.CurrentVersion}. " +
                "Upgrade StorageMaster before opening this database.");
        }
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection conn,
        ILogger logger,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        try
        {
            // Missing table is the only valid uninitialized state. A table (or
            // other object) with the expected name but an unreadable shape is
            // corruption/unsupported input and must not be replayed as v0.
            using var objectCmd = conn.CreateCommand();
            objectCmd.CommandText = """
                SELECT type
                FROM sqlite_master
                WHERE name = 'SchemaVersion' COLLATE NOCASE
                LIMIT 1;
                """;
            objectCmd.Transaction = transaction;
            var objectType = await objectCmd.ExecuteScalarAsync(ct);
            if (objectType is null or DBNull)
                return 0;
            if (objectType is not string type || !string.Equals(type, "table", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SchemaVersion exists but is not a table.");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            cmd.Transaction = transaction;
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull)
                throw new InvalidDataException("SchemaVersion exists but contains no version rows.");
            if (result is not long version || version < 1 || version > int.MaxValue)
                throw new InvalidDataException("SchemaVersion.Version must contain a positive 32-bit integer.");

            return (int)version;
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "SchemaVersion is malformed or unreadable.");
            throw new InvalidDataException("SchemaVersion is malformed or unreadable.", ex);
        }
    }

    /// <summary>
    /// Applies one migration level and stamps its schema version inside the
    /// caller's cross-process migration transaction.
    /// </summary>
    private static async Task ApplyMigrationAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string[] statements,
        int version,
        CancellationToken ct)
    {
        foreach (var sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // Stamp version inside the same transaction — atomic with DDL.
        using var stampCmd = conn.CreateCommand();
        stampCmd.CommandText = "INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES ($v, $t);";
        stampCmd.Transaction = transaction;
        stampCmd.Parameters.AddWithValue("$v", version);
        stampCmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        await stampCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(StorageDbContext));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Wait for a possible one-time initialization to finish. Repository
        // operations own and dispose their leases independently, so there is
        // no shared connection to close here. Semaphores intentionally remain
        // undisposed so in-flight operations can safely release them.
        await _coordination.InitializationLock.WaitAsync();
        try
        {
            Volatile.Write(ref _initialized, false);
        }
        finally
        {
            _coordination.InitializationLock.Release();
        }
    }

    private sealed class DatabaseCoordination
    {
        public SemaphoreSlim InitializationLock { get; } = new(1, 1);
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }
}
