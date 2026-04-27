using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using StorageMaster.Storage.Schema;

namespace StorageMaster.Storage;

/// <summary>
/// Manages the SQLite connection lifecycle and schema migration.
/// All repositories receive this as a dependency and call GetConnectionAsync
/// to obtain a ready-to-use connection.
///
/// WAL mode is enabled so readers never block writers and vice-versa,
/// which is critical during a scan that writes continuously.
/// </summary>
public sealed class StorageDbContext : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<StorageDbContext> _logger;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Serialises all transactional write operations so that concurrent scanner
    /// workers never call BeginTransactionAsync on the same SqliteConnection
    /// simultaneously (Microsoft.Data.Sqlite does not support nested transactions).
    /// </summary>
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public StorageDbContext(string dbPath, ILogger<StorageDbContext> logger)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    public async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        if (_initialized && _connection is not null)
            return _connection;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return _connection!;

            _connection = await OpenConnectionAsync(ct);
            await MigrateAsync(_connection, ct);
            _initialized = true;
            return _connection;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Cache      = SqliteCacheMode.Private,
        };

        var conn = new SqliteConnection(builder.ToString());
        await conn.OpenAsync(ct);

        // WAL mode: readers never block writers; essential for scan + UI concurrency.
        await ExecuteAsync(conn, "PRAGMA journal_mode=WAL;",          ct);
        await ExecuteAsync(conn, "PRAGMA synchronous=NORMAL;",        ct);
        await ExecuteAsync(conn, "PRAGMA foreign_keys=ON;",           ct);
        await ExecuteAsync(conn, "PRAGMA temp_store=MEMORY;",         ct);
        await ExecuteAsync(conn, "PRAGMA cache_size=-32000;",         ct); // 32 MB page cache

        _logger.LogInformation("Database opened: {Path}", _dbPath);
        return conn;
    }

    private async Task MigrateAsync(SqliteConnection conn, CancellationToken ct)
    {
        int current = await GetSchemaVersionAsync(conn, ct);

        if (current < DatabaseSchema.CurrentVersion)
        {
            _logger.LogInformation("Migrating schema from v{Current} to v{Target}",
                current, DatabaseSchema.CurrentVersion);

            if (current < 1)
                await ApplyStatementsAsync(conn, DatabaseSchema.V1Statements, ct);

            if (current < 2)
                await ApplyStatementsAsync(conn, DatabaseSchema.V2Statements, ct);

            await SetSchemaVersionAsync(conn, DatabaseSchema.CurrentVersion, ct);
        }
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        // SchemaVersion table may not exist yet on first launch.
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is DBNull or null ? 0 : Convert.ToInt32(result);
        }
        catch { return 0; }
    }

    private static async Task SetSchemaVersionAsync(SqliteConnection conn, int version, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES ($v, $t);";
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ApplyStatementsAsync(
        SqliteConnection conn,
        string[]         statements,
        CancellationToken ct)
    {
        using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText    = sql;
            cmd.Transaction    = (SqliteTransaction)tx;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }
        _initLock.Dispose();
        WriteLock.Dispose();
    }
}
