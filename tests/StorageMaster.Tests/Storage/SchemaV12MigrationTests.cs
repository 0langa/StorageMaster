using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

public sealed class SchemaV12MigrationTests : IAsyncDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"test_v12_{Guid.NewGuid():N}.db");
    private StorageDbContext? _context;

    [Fact]
    public async Task V11ToV12_AddsNullableIdentityAndLeavesHistoricalRowsUntrusted()
    {
        await CreateV11DatabaseAsync();
        _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using var connection = await _context.GetConnectionAsync();

        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            Convert.ToInt32(await versionCommand.ExecuteScalarAsync()).Should().Be(13);
        }

        using (var columnCommand = connection.CreateCommand())
        {
            columnCommand.CommandText = """
                SELECT COUNT(*)
                FROM pragma_table_info('FileEntries')
                WHERE name IN ('IdentityVolumeSerial', 'IdentityFileIndex');
                """;
            Convert.ToInt32(await columnCommand.ExecuteScalarAsync()).Should().Be(2);
        }

        var repository = new ScanRepository(_context);
        var historical = (await repository.GetLargestFilesAsync(1, 10)).Single();
        historical.Identity.Should().BeNull(
            "v11 rows have no trustworthy scan-time identity and must require a rescan before deletion");
    }

    private async Task CreateV11DatabaseAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE SchemaVersion (Version INTEGER NOT NULL, AppliedUtc TEXT NOT NULL);
            INSERT INTO SchemaVersion (Version, AppliedUtc)
            VALUES (11, '2026-01-01T00:00:00Z');

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

            CREATE TABLE FileEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
                FullPath TEXT NOT NULL,
                FileName TEXT NOT NULL,
                Extension TEXT NOT NULL DEFAULT '',
                SizeBytes INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL,
                ModifiedUtc TEXT NOT NULL,
                AccessedUtc TEXT NOT NULL,
                Attributes INTEGER NOT NULL DEFAULT 0,
                Category TEXT NOT NULL DEFAULT 'Unknown',
                IsReparsePoint INTEGER NOT NULL DEFAULT 0,
                NormalizedFullPath TEXT NOT NULL
            );
            INSERT INTO FileEntries
                (Id, SessionId, FullPath, FileName, Extension, SizeBytes,
                 CreatedUtc, ModifiedUtc, AccessedUtc, Attributes, Category,
                 IsReparsePoint, NormalizedFullPath)
            VALUES
                (1, 1, 'C:\legacy.bin', 'legacy.bin', '.bin', 4,
                 '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z',
                 '2026-01-01T00:00:00Z', 128, 'Unknown', 0, 'C:\LEGACY.BIN');
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
