using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// C4: Verifies that concurrent write operations don't produce SQLITE_BUSY
/// errors by confirming all write paths acquire WriteLock.
///
/// Strategy: launch many concurrent creates/updates/deletes and assert
/// none throw. Without WriteLock, this reliably produces SQLITE_BUSY on
/// a shared connection.
/// </summary>
public sealed class C4_WriteLockTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly ScanRepository _repo;

    public C4_WriteLockTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_c4_{Guid.NewGuid():N}.db");
        _ctx    = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _repo   = new ScanRepository(_ctx);
    }

    [Fact]
    public async Task ConcurrentCreatesAndUpdates_NoSqliteBusy()
    {
        // Warm up the connection + schema.
        var warmup = await _repo.CreateSessionAsync("C:\\warmup");

        // Launch concurrent creates + updates.
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            int capture = i;
            tasks.Add(Task.Run(async () =>
            {
                var session = await _repo.CreateSessionAsync($"C:\\path{capture}");
                var updated = session with
                {
                    Status        = ScanStatus.Completed,
                    CompletedUtc  = DateTime.UtcNow,
                    TotalFiles    = capture,
                    TotalSizeBytes = capture * 1024L,
                };
                await _repo.UpdateSessionAsync(updated);
            }));
        }

        // Should not throw SQLITE_BUSY.
        Func<Task> act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("all writes should be serialised by WriteLock");
    }

    [Fact]
    public async Task ConcurrentDeletesAndInserts_NoSqliteBusy()
    {
        // Create sessions, then concurrently delete some + insert files into others.
        var sessions = new List<ScanSession>();
        for (int i = 0; i < 10; i++)
            sessions.Add(await _repo.CreateSessionAsync($"C:\\del{i}"));

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            int capture = i;
            if (capture % 2 == 0)
            {
                tasks.Add(Task.Run(() => _repo.DeleteSessionAsync(sessions[capture].Id)));
            }
            else
            {
                tasks.Add(Task.Run(() => _repo.InsertFileEntriesAsync([
                    new FileEntry
                    {
                        Id         = 0,
                        SessionId  = sessions[capture].Id,
                        FullPath   = $"C:\\file{capture}.txt",
                        FileName   = $"file{capture}.txt",
                        Extension  = ".txt",
                        SizeBytes  = 1024,
                        CreatedUtc = DateTime.UtcNow,
                        ModifiedUtc = DateTime.UtcNow,
                        AccessedUtc = DateTime.UtcNow,
                        Attributes = FileAttributes.Normal,
                        Category   = FileTypeCategory.Document,
                    }
                ])));
            }
        }

        Func<Task> act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("mixed deletes + inserts should not produce SQLITE_BUSY");
    }

    [Fact]
    public async Task SettingsRepository_ConcurrentSaves_NoSqliteBusy()
    {
        var settingsRepo = new SettingsRepository(_ctx);
        var tasks = Enumerable.Range(0, 15)
            .Select(i => Task.Run(() => settingsRepo.SaveAsync(
                new AppSettings { DefaultScanPath = $"C:\\path{i}" })))
            .ToArray();

        Func<Task> act = () => Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("concurrent settings saves should be serialised");
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
