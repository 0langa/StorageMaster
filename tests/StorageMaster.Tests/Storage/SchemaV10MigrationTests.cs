using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;
using StorageMaster.Storage.Schema;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// v9 -> v10 upgrade: quarantine restore records must outlive duplicate and
/// scan-history rows. The physical quarantine file is independent of the scan
/// that discovered it, so deleting that scan must only clear MemberId.
/// </summary>
public sealed class SchemaV10MigrationTests : IAsyncDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"test_v10_{Guid.NewGuid():N}.db");
    private StorageDbContext? _ctx;

    [Fact]
    public async Task V10Migration_DeleteSession_PreservesQuarantineRowAndClearsMemberId()
    {
        await CreateV9DatabaseWithQuarantineRowAsync();

        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        var scanRepository = new ScanRepository(_ctx);
        await using var conn = await _ctx.GetConnectionAsync();

        await scanRepository.DeleteSessionAsync(1);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT MemberId, RunId, OriginalPath, QuarantinePath
                FROM QuarantinedFiles
                WHERE Id = 5;
                """;
            using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue(
                "scan-history cleanup must not destroy a physical quarantine restore point");
            reader.IsDBNull(0).Should().BeTrue("the deleted duplicate member should be detached");
            reader.GetInt64(1).Should().Be(7);
            reader.GetString(2).Should().Be(@"C:\dup.bin");
            reader.GetString(3).Should().Be(@"C:\q\7\dup.bin");
        }

        using (var versionCmd = conn.CreateCommand())
        {
            versionCmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            Convert.ToInt32(await versionCmd.ExecuteScalarAsync()).Should().Be(DatabaseSchema.CurrentVersion);
        }

        using (var fkCmd = conn.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_key_check;";
            using var reader = await fkCmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeFalse("the migrated schema must remain FK-consistent");
        }
    }

    private async Task CreateV9DatabaseWithQuarantineRowAsync()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();

        string[][] levels =
        [
            DatabaseSchema.V1Statements, DatabaseSchema.V2Statements,
            DatabaseSchema.V3Statements, DatabaseSchema.V4Statements,
            DatabaseSchema.V5Statements, DatabaseSchema.V6Statements,
            DatabaseSchema.V7Statements, DatabaseSchema.V8Statements,
            DatabaseSchema.V9Statements,
        ];

        for (var version = 1; version <= levels.Length; version++)
        {
            foreach (var sql in levels[version - 1])
                await ExecAsync(conn, sql);
            await ExecAsync(conn,
                $"INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES ({version}, '2026-01-01T00:00:00Z');");
        }

        await ExecAsync(conn, "PRAGMA foreign_keys=ON;");
        await ExecAsync(conn, "INSERT INTO ScanSessions (Id, RootPath, StartedUtc) VALUES (1, 'C:\\', '2026-01-01T00:00:00Z');");
        await ExecAsync(conn, """
            INSERT INTO FileEntries
                (Id, SessionId, FullPath, FileName, CreatedUtc, ModifiedUtc, AccessedUtc, NormalizedFullPath)
            VALUES
                (1, 1, 'C:\dup.bin', 'dup.bin', '2026-01-01T00:00:00Z',
                 '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'C:\DUP.BIN');
            """);
        await ExecAsync(conn, "INSERT INTO DuplicateRuns (Id, SessionId, StartedUtc, Status, ConfigJson) VALUES (7, 1, '2026-01-01T00:00:00Z', 'Completed', '{}');");
        await ExecAsync(conn, "INSERT INTO DuplicateGroups (Id, RunId, Method, Algorithm, Confidence, RepresentativeFileEntryId) VALUES (1, 7, 'ExactSha256', 'SHA-256', 1.0, 1);");
        await ExecAsync(conn, """
            INSERT INTO DuplicateGroupMembers
                (Id, GroupId, FileEntryId, FullPath, FileName, ModifiedUtc)
            VALUES
                (42, 1, 1, 'C:\dup.bin', 'dup.bin', '2026-01-01T00:00:00Z');
            """);
        await ExecAsync(conn, """
            INSERT INTO QuarantinedFiles
                (Id, MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc)
            VALUES
                (5, 42, 7, 'C:\dup.bin', 'C:\q\7\dup.bin', '2026-01-02T00:00:00Z');
            """);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null)
            await _ctx.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
