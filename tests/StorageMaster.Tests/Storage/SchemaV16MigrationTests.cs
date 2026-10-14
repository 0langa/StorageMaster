using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;
using StorageMaster.Storage.Schema;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// Schema v16 adds <c>FileEntries.IsAdditionalHardLink</c>. An existing database is
/// upgraded in place, so the migration must keep every row and every total exactly
/// as the user saw them before.
/// </summary>
public sealed class SchemaV16MigrationTests : IAsyncDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"test_v16_{Guid.NewGuid():N}.db");
    private StorageDbContext? _context;

    [Fact]
    public async Task V15ToV16_KeepsEveryRowAndEveryTotal()
    {
        await CreateV15DatabaseWithScanAsync();

        _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        var repository = new ScanRepository(_context);

        await using (var connection = await _context.GetConnectionAsync())
        {
            (await ScalarAsync(connection, "SELECT MAX(Version) FROM SchemaVersion;"))
                .Should().Be(DatabaseSchema.CurrentVersion);
            (await ScalarAsync(connection, "SELECT COUNT(*) FROM FileEntries;")).Should().Be(4);
            (await ScalarAsync(connection, "SELECT COUNT(*) FROM FileEntries WHERE IsAdditionalHardLink <> 0;"))
                .Should().Be(0, "rows written before v16 were never marked");

            using var columns = connection.CreateCommand();
            columns.CommandText =
                "SELECT \"notnull\", dflt_value FROM pragma_table_info('FileEntries') WHERE name = 'IsAdditionalHardLink';";
            using var reader = await columns.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue("v16 adds the column");
            reader.GetInt64(0).Should().Be(1);
            reader.GetString(1).Should().Be("0");
        }

        // Older scans keep counting every name, exactly as they were displayed: the
        // two names that share an identity were both counted before the upgrade.
        var breakdown = await repository.GetCategoryBreakdownAsync(1);
        breakdown[FileTypeCategory.Image].Should().Be((3L, 2010L));
        breakdown[FileTypeCategory.Document].Should().Be((1L, 7L));

        // New rows go in alongside the old ones.
        await repository.InsertFileEntriesAsync(
        [
            new FileEntry
            {
                Id = 0,
                SessionId = 1,
                FullPath = @"C:\data\new\a.jpg",
                FileName = "a.jpg",
                Extension = ".jpg",
                SizeBytes = 1000,
                CreatedUtc = DateTime.UtcNow,
                ModifiedUtc = DateTime.UtcNow,
                AccessedUtc = DateTime.UtcNow,
                Attributes = FileAttributes.Archive,
                Category = FileTypeCategory.Image,
                Identity = new FileIdentity("VOL1", 1),
                IsAdditionalHardLink = true,
            },
        ]);
        (await repository.GetCategoryBreakdownAsync(1))[FileTypeCategory.Image]
            .Should().Be((4L, 2010L), "an additional hard link adds a name but no bytes");
    }

    [Fact]
    public async Task V16_OpeningAgainDoesNotMigrateTwice()
    {
        await CreateV15DatabaseWithScanAsync();

        await using (var first = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance))
        await using (var connection = await first.GetConnectionAsync())
        {
            (await ScalarAsync(connection, "SELECT MAX(Version) FROM SchemaVersion;"))
                .Should().Be(DatabaseSchema.CurrentVersion);
        }

        SqliteConnection.ClearAllPools();
        _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using var reopened = await _context.GetConnectionAsync();

        (await ScalarAsync(reopened, "SELECT COUNT(*) FROM SchemaVersion WHERE Version = 16;"))
            .Should().Be(1, "the column is added once; a second ALTER TABLE would fail");
        (await ScalarAsync(reopened, "SELECT COUNT(*) FROM FileEntries;")).Should().Be(4);
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();

        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
            File.Delete(_dbPath + "-wal");
            File.Delete(_dbPath + "-shm");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A database exactly as v15 left it: every migration level up to 15 applied in
    /// order, then a scan written with the v15 column set, including two names that
    /// share one file identity.
    /// </summary>
    private async Task CreateV15DatabaseWithScanAsync()
    {
        string[][] levels =
        [
            DatabaseSchema.V1Statements, DatabaseSchema.V2Statements, DatabaseSchema.V3Statements,
            DatabaseSchema.V4Statements, DatabaseSchema.V5Statements, DatabaseSchema.V6Statements,
            DatabaseSchema.V7Statements, DatabaseSchema.V8Statements, DatabaseSchema.V9Statements,
            DatabaseSchema.V10Statements, DatabaseSchema.V11Statements, DatabaseSchema.V12Statements,
            DatabaseSchema.V13Statements, DatabaseSchema.V14Statements, DatabaseSchema.V15Statements,
        ];

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        using (var transaction = connection.BeginTransaction())
        {
            for (var level = 0; level < levels.Length; level++)
            {
                foreach (var sql in levels[level])
                    await ExecuteAsync(connection, transaction, sql);

                await ExecuteAsync(
                    connection,
                    transaction,
                    $"INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES ({level + 1}, '2026-01-01T00:00:00Z');");
            }

            transaction.Commit();
        }

        await ExecuteAsync(connection, null, """
            INSERT INTO ScanSessions (Id, RootPath, StartedUtc, Status)
            VALUES (1, 'C:\data', '2026-01-01T00:00:00Z', 'Completed');

            INSERT INTO FileEntries
                (SessionId, FullPath, FileName, Extension, SizeBytes,
                 CreatedUtc, ModifiedUtc, AccessedUtc, Attributes, Category, IsReparsePoint,
                 NormalizedFullPath, IdentityVolumeSerial, IdentityFileIndex)
            VALUES
                (1, 'C:\data\a.jpg', 'a.jpg', '.jpg', 1000,
                 '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 32, 'Image', 0,
                 'C:\DATA\A.JPG', 'VOL1', '1'),
                (1, 'C:\data\link\a.jpg', 'a.jpg', '.jpg', 1000,
                 '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 32, 'Image', 0,
                 'C:\DATA\LINK\A.JPG', 'VOL1', '1'),
                (1, 'C:\data\b.jpg', 'b.jpg', '.jpg', 10,
                 '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 32, 'Image', 0,
                 'C:\DATA\B.JPG', 'VOL1', '2'),
                (1, 'C:\data\c.txt', 'c.txt', '.txt', 7,
                 '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 32, 'Document', 0,
                 'C:\DATA\C.TXT', NULL, NULL);
            """);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
