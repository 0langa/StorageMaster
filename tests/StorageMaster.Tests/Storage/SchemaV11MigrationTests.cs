using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;
using StorageMaster.Storage.Schema;

namespace StorageMaster.Tests.Storage;

public sealed class SchemaV11MigrationTests : IAsyncDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"test_v11_{Guid.NewGuid():N}.db");
    private StorageDbContext? _context;

    [Fact]
    public async Task V10ToV11_CollapsesCaseVariantsAndAddsNormalizedIdentityConstraint()
    {
        await CreateV10DatabaseWithCaseVariantsAsync();
        _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using var connection = await _context.GetConnectionAsync();

        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            Convert.ToInt32(await versionCommand.ExecuteScalarAsync()).Should().Be(DatabaseSchema.CurrentVersion);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, FullPath, FolderName, DirectSizeBytes, TotalSizeBytes,
                       FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied,
                       NormalizedFullPath
                FROM FolderEntries
                WHERE SessionId = 1
                ORDER BY Id;
                """;
            using var reader = await command.ExecuteReaderAsync();

            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(5, "the oldest row id is the deterministic survivor");
            reader.GetString(1).Should().Be(@"C:\Data");
            reader.GetString(2).Should().Be("Data");
            reader.GetInt64(3).Should().Be(150,
                "migration keeps the largest observed direct size rather than summing duplicate observations");
            reader.GetInt64(4).Should().Be(500);
            reader.GetInt32(5).Should().Be(6);
            reader.GetInt32(6).Should().Be(3);
            reader.GetInt32(7).Should().Be(1);
            reader.GetInt32(8).Should().Be(1);
            reader.GetString(9).Should().Be(@"C:\DATA");
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(12);
            (await reader.ReadAsync()).Should().BeFalse();
        }

        var duplicateInsert = async () =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO FolderEntries
                    (SessionId, FullPath, FolderName, DirectSizeBytes, TotalSizeBytes,
                     FileCount, SubFolderCount, IsReparsePoint, WasAccessDenied, NormalizedFullPath)
                VALUES
                    (1, 'c:\data', 'data', 1, 1, 1, 0, 0, 0, 'C:\DATA');
                """;
            await command.ExecuteNonQueryAsync();
        };
        await duplicateInsert.Should().ThrowAsync<SqliteException>(
            "v11 must enforce one normalized folder identity per scan session");

        using var foreignKeyCommand = connection.CreateCommand();
        foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
        using var foreignKeyReader = await foreignKeyCommand.ExecuteReaderAsync();
        (await foreignKeyReader.ReadAsync()).Should().BeFalse();
    }

    private async Task CreateV10DatabaseWithCaseVariantsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE SchemaVersion (Version INTEGER NOT NULL, AppliedUtc TEXT NOT NULL);
            INSERT INTO SchemaVersion (Version, AppliedUtc)
            VALUES (10, '2026-01-01T00:00:00Z');

            CREATE TABLE ScanSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RootPath TEXT NOT NULL,
                StartedUtc TEXT NOT NULL
            );
            INSERT INTO ScanSessions (Id, RootPath, StartedUtc)
            VALUES (1, 'C:\', '2026-01-01T00:00:00Z');

            CREATE TABLE FileEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT
            );

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
                NormalizedFullPath TEXT,
                UNIQUE (SessionId, FullPath)
            );

            INSERT INTO FolderEntries VALUES
                (5, 1, 'C:\Data', 'Data', 100, 400, 4, 2, 0, 0, 'C:\DATA'),
                (7, 1, 'c:\data', 'data', 90, 500, 6, 1, 1, 0, 'C:\DATA'),
                (9, 1, 'C:\DATA', 'DATA', 150, 450, 5, 3, 0, 1, 'C:\DATA'),
                (12, 1, 'C:\Other', 'Other', 25, 25, 1, 0, 0, 0, 'C:\OTHER');
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
