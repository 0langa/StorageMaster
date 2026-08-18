using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;
using StorageMaster.Storage.Schema;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// v8 → v9 upgrade: QuarantinedFiles is rebuilt so MemberId becomes nullable.
/// Existing quarantine rows (real user restore points) must survive the
/// rebuild byte-for-byte, and the rebuilt table must accept NULL MemberId
/// while still enforcing the FK for non-null values.
/// </summary>
public sealed class SchemaV9MigrationTests : IAsyncDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"test_v9_{Guid.NewGuid():N}.db");
    private StorageDbContext? _ctx;

    private async Task<SqliteConnection> CreateV8DatabaseWithQuarantineRowAsync()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();

        // Apply the real v1..v8 statements + stamps, exactly like an app at v8.
        string[][] levels =
        [
            DatabaseSchema.V1Statements, DatabaseSchema.V2Statements,
            DatabaseSchema.V3Statements, DatabaseSchema.V4Statements,
            DatabaseSchema.V5Statements, DatabaseSchema.V6Statements,
            DatabaseSchema.V7Statements, DatabaseSchema.V8Statements,
        ];
        for (int version = 1; version <= levels.Length; version++)
        {
            foreach (var sql in levels[version - 1])
                await ExecAsync(conn, sql);
            await ExecAsync(conn,
                $"INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES ({version}, '2026-01-01T00:00:00Z');");
        }

        // Minimal duplicate chain so the quarantine row has a valid member FK.
        await ExecAsync(conn, "INSERT INTO ScanSessions (Id, RootPath, StartedUtc) VALUES (1, 'C:\\', '2026-01-01T00:00:00Z');");
        await ExecAsync(conn, """
            INSERT INTO FileEntries (Id, SessionId, FullPath, FileName, CreatedUtc, ModifiedUtc, AccessedUtc, NormalizedFullPath)
            VALUES (1, 1, 'C:\dup.bin', 'dup.bin', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'C:\DUP.BIN');
            """);
        await ExecAsync(conn, "INSERT INTO DuplicateRuns (Id, SessionId, StartedUtc, Status, ConfigJson) VALUES (7, 1, '2026-01-01T00:00:00Z', 'Completed', '{}');");
        await ExecAsync(conn, "INSERT INTO DuplicateGroups (Id, RunId, Method, Algorithm, Confidence, RepresentativeFileEntryId) VALUES (1, 7, 'ExactSha256', 'SHA-256', 1.0, 1);");
        await ExecAsync(conn, """
            INSERT INTO DuplicateGroupMembers (Id, GroupId, FileEntryId, FullPath, FileName, ModifiedUtc)
            VALUES (42, 1, 1, 'C:\dup.bin', 'dup.bin', '2026-01-01T00:00:00Z');
            """);
        await ExecAsync(conn, """
            INSERT INTO QuarantinedFiles (Id, MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc)
            VALUES (5, 42, 7, 'C:\dup.bin', 'C:\q\7\dup.bin', '2026-01-02T00:00:00Z');
            """);

        return conn;
    }

    [Fact]
    public async Task V9Migration_PreservesExistingQuarantineRows_AndAllowsNullMemberId()
    {
        var setup = await CreateV8DatabaseWithQuarantineRowAsync();
        await setup.CloseAsync();
        await setup.DisposeAsync();
        SqliteConnection.ClearAllPools();

        // Opening through StorageDbContext runs the v9 migration.
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        await using var conn = await _ctx.GetConnectionAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT MemberId, RunId, OriginalPath, QuarantinePath FROM QuarantinedFiles WHERE Id = 5;";
            using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue("the pre-migration quarantine row must survive the rebuild");
            reader.GetInt64(0).Should().Be(42);
            reader.GetInt64(1).Should().Be(7);
            reader.GetString(2).Should().Be(@"C:\dup.bin");
            reader.GetString(3).Should().Be(@"C:\q\7\dup.bin");
        }

        // NULL MemberId (generic cleanup) is now accepted.
        await ExecAsync(conn, """
            INSERT INTO QuarantinedFiles (MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc)
            VALUES (NULL, 0, 'C:\temp\junk.log', 'C:\q\0\junk.log', '2026-01-03T00:00:00Z');
            """);

        // A bogus non-null MemberId is still rejected by the FK.
        var insertBogus = async () => await ExecAsync(conn, """
            INSERT INTO QuarantinedFiles (MemberId, RunId, OriginalPath, QuarantinePath, QuarantinedUtc)
            VALUES (999999, 0, 'C:\x', 'C:\q\x', '2026-01-03T00:00:00Z');
            """);
        await insertBogus.Should().ThrowAsync<SqliteException>("the member FK must survive the table rebuild");
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
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
