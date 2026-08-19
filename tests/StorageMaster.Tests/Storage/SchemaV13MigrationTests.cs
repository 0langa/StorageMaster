using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;
using StorageMaster.Storage.Schema;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// Schema v13 materialises each folder's parent so tree and drill-down queries
/// become indexed equality lookups instead of full session scans.
/// </summary>
public sealed class SchemaV13MigrationTests : IAsyncDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"test_v13_{Guid.NewGuid():N}.db");
    private StorageDbContext? _context;

    [Fact]
    public async Task V12ToV13_BackfillsParentPathsForExistingFolders()
    {
        await CreateV12DatabaseAsync();
        _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using var connection = await _context.GetConnectionAsync();

        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            Convert.ToInt32(await versionCommand.ExecuteScalarAsync()).Should().Be(DatabaseSchema.CurrentVersion);
        }

        var parents = await ReadParentsAsync(connection);

        parents["C:\\"].Should().BeNull("a drive root has no parent inside the scan");
        parents["C:\\USERS"].Should().Be("C:\\", "a drive root keeps its separator");
        parents["C:\\USERS\\JULIUS"].Should().Be("C:\\USERS");
        parents["C:\\USERS\\JULIUS\\ÄÖÜ"].Should().Be(
            "C:\\USERS\\JULIUS",
            "the backfill is a pure substring and must not depend on SQLite's ASCII-only upper()");
    }

    [Fact]
    public async Task V13_ChildLookupUsesTheParentIndex()
    {
        await CreateV12DatabaseAsync();
        _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using var connection = await _context.GetConnectionAsync();

        using var plan = connection.CreateCommand();
        plan.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT * FROM FolderEntries
            WHERE SessionId = 1 AND ParentNormalizedPath = 'C:\USERS'
            ORDER BY TotalSizeBytes DESC;
            """;

        var steps = new List<string>();
        using (var reader = await plan.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                steps.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        steps.Should().ContainSingle()
            .Which.Should().Contain("IX_FolderEntries_Session_Parent_Size")
            .And.Contain("ParentNormalizedPath=?",
                "a child lookup must be an indexed equality, never a session-wide scan");
    }

    private static async Task<Dictionary<string, string?>> ReadParentsAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT NormalizedFullPath, ParentNormalizedPath FROM FolderEntries;";

        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            parents[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return parents;
    }

    private async Task CreateV12DatabaseAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE SchemaVersion (Version INTEGER NOT NULL, AppliedUtc TEXT NOT NULL);
            INSERT INTO SchemaVersion (Version, AppliedUtc)
            VALUES (12, '2026-01-01T00:00:00Z');

            CREATE TABLE ScanSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RootPath TEXT NOT NULL,
                StartedUtc TEXT NOT NULL,
                CompletedUtc TEXT,
                Status TEXT NOT NULL DEFAULT 'Running',
                TotalSizeBytes INTEGER NOT NULL DEFAULT 0,
                TotalFiles INTEGER NOT NULL DEFAULT 0,
                TotalFolders INTEGER NOT NULL DEFAULT 0,
                AccessDeniedCount INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT
            );
            INSERT INTO ScanSessions (Id, RootPath, StartedUtc, Status)
            VALUES (1, 'C:\', '2026-01-01T00:00:00Z', 'Completed');

            CREATE TABLE FolderEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
                FullPath TEXT NOT NULL,
                FolderName TEXT NOT NULL,
                DirectSizeBytes INTEGER NOT NULL DEFAULT 0,
                TotalSizeBytes INTEGER NOT NULL DEFAULT 0,
                FileCount INTEGER NOT NULL DEFAULT 0,
                SubFolderCount INTEGER NOT NULL DEFAULT 0,
                IsReparsePoint INTEGER NOT NULL DEFAULT 0,
                WasAccessDenied INTEGER NOT NULL DEFAULT 0,
                NormalizedFullPath TEXT NOT NULL,
                UNIQUE (SessionId, NormalizedFullPath)
            );
            INSERT INTO FolderEntries
                (SessionId, FullPath, FolderName, NormalizedFullPath)
            VALUES
                (1, 'C:\',                     'C:\',    'C:\'),
                (1, 'C:\Users',                'Users',  'C:\USERS'),
                (1, 'C:\Users\Julius',         'Julius', 'C:\USERS\JULIUS'),
                (1, 'C:\Users\Julius\Äöü',     'Äöü',    'C:\USERS\JULIUS\ÄÖÜ');
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
