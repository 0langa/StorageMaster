using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;

namespace StorageMaster.Tests.Storage;

public sealed class StorageDbContextSchemaValidationTests
{
    [Fact]
    public async Task MalformedSchemaVersion_FailsClosed_DisposesInitializationConnection_AndCanRetry()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"schema_malformed_{Guid.NewGuid():N}.db");
        var context = new StorageDbContext(dbPath, NullLogger<StorageDbContext>.Instance);

        try
        {
            await using (var setup = new SqliteConnection($"Data Source={dbPath}"))
            {
                await setup.OpenAsync();
                using var cmd = setup.CreateCommand();
                cmd.CommandText = "CREATE TABLE SchemaVersion (AppliedUtc TEXT NOT NULL);";
                await cmd.ExecuteNonQueryAsync();
            }

            var open = async () =>
            {
                await using var connection = await context.GetConnectionAsync();
            };

            await open.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*SchemaVersion*");
            await using (var repair = new SqliteConnection($"Data Source={dbPath}"))
            {
                await repair.OpenAsync();
                using var cmd = repair.CreateCommand();
                cmd.CommandText = "DROP TABLE SchemaVersion;";
                await cmd.ExecuteNonQueryAsync();
            }

            await using var connection = await context.GetConnectionAsync();
            using var versionCmd = connection.CreateCommand();
            versionCmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            Convert.ToInt32(await versionCmd.ExecuteScalarAsync()).Should().Be(13,
                "the same context should retry cleanly after the database is repaired");
        }
        finally
        {
            await context.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task FutureSchemaVersion_IsRejectedWithoutChangingTheDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"schema_future_{Guid.NewGuid():N}.db");
        var context = new StorageDbContext(dbPath, NullLogger<StorageDbContext>.Instance);

        try
        {
            await using (var setup = new SqliteConnection($"Data Source={dbPath}"))
            {
                await setup.OpenAsync();
                using var cmd = setup.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE SchemaVersion (Version INTEGER NOT NULL, AppliedUtc TEXT NOT NULL);
                    INSERT INTO SchemaVersion (Version, AppliedUtc) VALUES (999, '2026-01-01T00:00:00Z');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var open = async () =>
            {
                await using var connection = await context.GetConnectionAsync();
            };

            await open.Should().ThrowAsync<NotSupportedException>()
                .WithMessage("*999*newer*");

            await using var verification = new SqliteConnection($"Data Source={dbPath}");
            await verification.OpenAsync();
            using var versionCmd = verification.CreateCommand();
            versionCmd.CommandText = "SELECT MAX(Version) FROM SchemaVersion;";
            Convert.ToInt32(await versionCmd.ExecuteScalarAsync()).Should().Be(999,
                "rejecting a future schema must not rewrite or downgrade it");
        }
        finally
        {
            await context.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

}
