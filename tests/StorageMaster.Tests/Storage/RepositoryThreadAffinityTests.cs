using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

/// <summary>
/// Guards the fix for repository work running on the UI thread.
/// <para>
/// Microsoft.Data.Sqlite has no real asynchronous I/O: <c>SqliteConnection</c> and
/// <c>SqliteDataReader</c> declare no async methods, and <c>ExecuteReaderAsync</c>
/// wraps the synchronous call in an already-completed task. Awaiting a completed
/// task continues inline, so before this fix a repository call made from the UI
/// thread executed its entire query on the UI thread — the <c>await</c> looked
/// asynchronous and was not. Large scans froze navigation as a result.
/// </para>
/// <para>
/// These tests install a synchronization context that behaves like the UI
/// dispatcher (single pump thread) and assert that repository work does not run on
/// it. Without <c>ConfigureAwait(false)</c> in the repositories, or without the
/// thread hop in <see cref="StorageDbContext.GetConnectionAsync"/>, they fail.
/// </para>
/// </summary>
public sealed class RepositoryThreadAffinityTests : IAsyncDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"test_affinity_{Guid.NewGuid():N}.db");
    private StorageDbContext? _context;

    [Fact]
    public async Task ReadQueryDoesNotRunOnTheCallingSynchronizationContext()
    {
        await RunOnFakeUiThreadAsync(async () =>
        {
            _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
            var repository = new ScanRepository(_context);

            // Invoke without awaiting. If the query ran inline on this thread, the
            // returned task would already be completed by the time control comes
            // back — that is precisely the old behaviour. A task still pending here
            // proves the work was handed to the thread pool instead.
            var pending = repository.GetRecentSessionsAsync(5);
            var ranInline = pending.IsCompleted;

            await pending;

            ranInline.Should().BeFalse(
                "a repository read must not execute inline on the UI thread; "
                + "Microsoft.Data.Sqlite is synchronous, so inline execution blocks the "
                + "dispatcher for the whole query");
        });
    }

    [Fact]
    public async Task WriteQueryDoesNotRunOnTheCallingSynchronizationContext()
    {
        await RunOnFakeUiThreadAsync(async () =>
        {
            _context = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
            var repository = new ScanRepository(_context);

            var pending = repository.CreateSessionAsync(@"C:\Affinity");
            var ranInline = pending.IsCompleted;

            await pending;

            ranInline.Should().BeFalse(
                "session creation takes the write lock and opens a connection; doing that "
                + "inline would hold the UI thread for the duration");
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> under a single-threaded synchronization context,
    /// which is how a WinUI dispatcher behaves: continuations posted to it come back
    /// to the one pump thread.
    /// </summary>
    private static async Task RunOnFakeUiThreadAsync(Func<Task> body)
    {
        var context = new SingleThreadSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var thread = new Thread(context.Pump) { IsBackground = true };
        thread.Start();

        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        context.Post(async _ =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                await body();
                completion.SetResult(null);
            }
            catch (Exception ex)
            {
                completion.SetResult(ex);
            }
            finally
            {
                context.Complete();
            }
        }, null);

        var failure = await completion.Task;
        SynchronizationContext.SetSynchronizationContext(previous);

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
            await _context.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // Pump already completed; the work is no longer needed.
            }
        }

        public void Pump()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }

        public void Complete() => _queue.CompleteAdding();
    }
}
