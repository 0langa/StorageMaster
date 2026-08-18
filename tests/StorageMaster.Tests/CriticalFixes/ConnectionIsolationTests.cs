using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.CriticalFixes;

public sealed class ConnectionIsolationTests : IAsyncDisposable
{
    private const int WriterCount = 6;
    private const int FilesPerWriter = 5;

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"connection_isolation_{Guid.NewGuid():N}.db");
    private readonly StorageDbContext _context;
    private readonly ScanRepository _repository;

    public ConnectionIsolationTests()
    {
        _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _repository = new ScanRepository(_context);
    }

    [Fact]
    public async Task GetConnectionAsync_ReturnsIndependentConfiguredLeases()
    {
        await using var first = await _context.GetConnectionAsync();
        await using var second = await _context.GetConnectionAsync();

        first.Should().NotBeSameAs(second,
            "each operation must own a connection that another operation cannot concurrently mutate or dispose");
        first.DefaultTimeout.Should().Be(30,
            "uncoordinated or cross-process SQLite writers need a bounded busy wait");
        (await ReadPragmaIntAsync(first, "foreign_keys")).Should().Be(1);
        (await ReadPragmaIntAsync(second, "foreign_keys")).Should().Be(1);
    }

    [Fact]
    public async Task ParallelReadWriteCancelAndLeaseDisposal_PreservesRows()
    {
        var session = await _repository.CreateSessionAsync(@"C:\stress");
        await _repository.InsertFileEntriesAsync([
            MakeFile(session.Id, @"C:\stress\seed.bin", 1),
        ]);

        // Keep a read transaction and reader alive throughout the stress run.
        // WAL permits writers on other connections; a shared SqliteConnection
        // instead faults on nested transactions/concurrent readers/disposal.
        await using var heldReadConnection = await _context.GetConnectionAsync();
        (await ReadPragmaTextAsync(heldReadConnection, "journal_mode")).Should().Be("wal");
        await using var heldReadTransaction = heldReadConnection.BeginTransaction(deferred: true);
        using var heldReadCommand = heldReadConnection.CreateCommand();
        heldReadCommand.Transaction = heldReadTransaction;
        heldReadCommand.CommandText = "SELECT Id FROM FileEntries WHERE SessionId = $sid ORDER BY Id;";
        heldReadCommand.Parameters.AddWithValue("$sid", session.Id);

        await using (var heldReader = await heldReadCommand.ExecuteReaderAsync())
        {
            (await heldReader.ReadAsync()).Should().BeTrue();

            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var writers = Enumerable.Range(0, WriterCount)
                .Select(writer => Task.Run(async () =>
                {
                    await start.Task;
                    var entries = Enumerable.Range(0, FilesPerWriter)
                        .Select(file => MakeFile(
                            session.Id,
                            $@"C:\stress\writer-{writer}\file-{file}.bin",
                            writer * 100 + file + 10L))
                        .ToArray();
                    await _repository.InsertFileEntriesAsync(entries);
                }))
                .ToArray();

            var readers = Enumerable.Range(0, 6)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    for (var iteration = 0; iteration < 10; iteration++)
                    {
                        await _repository.GetLargestFilesAsync(session.Id, 25);
                        (await _repository.GetSessionAsync(session.Id)).Should().NotBeNull();
                    }
                }))
                .ToArray();

            var cancellations = Enumerable.Range(0, 6)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    using var cts = new CancellationTokenSource();
                    cts.Cancel();
                    var canceledRead = async () =>
                        await _repository.GetLargestFilesAsync(session.Id, 5, cts.Token);
                    await canceledRead.Should().ThrowAsync<OperationCanceledException>();
                }))
                .ToArray();

            var leaseUsers = Enumerable.Range(0, 12)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    await using var connection = await _context.GetConnectionAsync();
                    (await ReadPragmaIntAsync(connection, "foreign_keys")).Should().Be(1);
                }))
                .ToArray();

            start.SetResult(true);
            var allTasks = writers.Concat(readers).Concat(cancellations).Concat(leaseUsers).ToArray();
            try
            {
                await Task.WhenAll(allTasks).WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"Connection stress timed out. " +
                    $"Writers={FormatStatuses(writers)}; " +
                    $"Readers={FormatStatuses(readers)}; " +
                    $"Cancellations={FormatStatuses(cancellations)}; " +
                    $"LeaseUsers={FormatStatuses(leaseUsers)}.",
                    ex);
            }
        }

        await heldReadTransaction.CommitAsync();

        await using var verification = await _context.GetConnectionAsync();
        using var countCommand = verification.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM FileEntries WHERE SessionId = $sid;";
        countCommand.Parameters.AddWithValue("$sid", session.Id);
        Convert.ToInt32(await countCommand.ExecuteScalarAsync()).Should().Be(
            1 + WriterCount * FilesPerWriter,
            "every serialized writer batch must commit exactly once despite concurrent readers and cancellations");
    }

    [Fact]
    public async Task IndependentContexts_FirstUseAndConcurrentWrites_PreserveEveryRow()
    {
        await using var secondContext =
            new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        var secondRepository = new ScanRepository(secondContext);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var createFirst = Task.Run(async () =>
        {
            await start.Task;
            return await _repository.CreateSessionAsync(@"C:\context-a");
        });
        var createSecond = Task.Run(async () =>
        {
            await start.Task;
            return await secondRepository.CreateSessionAsync(@"C:\context-b");
        });

        start.SetResult(true);
        var sessions = await Task.WhenAll(createFirst, createSecond)
            .WaitAsync(TimeSpan.FromSeconds(10));

        const int batchCount = 12;
        const int filesPerBatch = 20;
        var writes = Enumerable.Range(0, batchCount)
            .Select(batch => Task.Run(() =>
            {
                var repository = batch % 2 == 0 ? _repository : secondRepository;
                var session = sessions[batch % 2];
                return repository.InsertFileEntriesAsync(
                    Enumerable.Range(0, filesPerBatch)
                        .Select(file => MakeFile(
                            session.Id,
                            $@"C:\context-{batch % 2}\batch-{batch}\file-{file}.bin",
                            batch * 1_000L + file))
                        .ToArray());
            }))
            .ToArray();

        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(20));

        var firstRows = await _repository.GetLargestFilesAsync(sessions[0].Id, batchCount * filesPerBatch);
        var secondRows = await secondRepository.GetLargestFilesAsync(sessions[1].Id, batchCount * filesPerBatch);
        firstRows.Should().HaveCount(batchCount / 2 * filesPerBatch);
        secondRows.Should().HaveCount(batchCount / 2 * filesPerBatch);
    }

    private static FileEntry MakeFile(long sessionId, string path, long size) => new()
    {
        Id = 0,
        SessionId = sessionId,
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        SizeBytes = size,
        CreatedUtc = DateTime.UtcNow,
        ModifiedUtc = DateTime.UtcNow,
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Unknown,
    };

    private static async Task<int> ReadPragmaIntAsync(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadPragmaTextAsync(SqliteConnection connection, string pragma)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToString(await command.ExecuteScalarAsync())!;
    }

    private static string FormatStatuses(IEnumerable<Task> tasks) =>
        string.Join(',', tasks.GroupBy(static task => task.Status)
            .Select(static group => $"{group.Key}:{group.Count()}"));

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
