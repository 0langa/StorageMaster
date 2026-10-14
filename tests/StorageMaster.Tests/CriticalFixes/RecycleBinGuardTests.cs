using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// Recycle Bin mode must never delete permanently. IFileOperation with
/// FOF_NOCONFIRMATION answers the shell's own "this cannot be recycled, delete it
/// permanently?" question with yes, so before the guard a file on a drive with no
/// Recycle Bin was destroyed while the deleter reported "moved to the Recycle Bin".
///
/// A loopback administrative share (\\localhost\C$\...) reaches an ordinary temp
/// file through a network path, and network paths have no Recycle Bin, so it
/// reproduces that case without changing any system setting. Machines without the
/// share (no Server service, no admin rights) skip those tests.
/// </summary>
public sealed class RecycleBinGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"sm_recycle_guard_{Guid.NewGuid():N}");

    private readonly FileDeleter _deleter = new(
        NullLogger<FileDeleter>.Instance,
        new FileSnapshotProvider());

    public RecycleBinGuardTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DeleteAsync_RecycleBinWhereThereIsNoRecycleBin_RefusesAndKeepsFile()
    {
        var local = CreateFile("no-bin.txt");
        if (!TryGetLoopbackSharePath(local, out var networkPath))
            return;

        var outcome = await _deleter.DeleteAsync(
            new DeletionRequest(networkPath, DeletionMethod.RecycleBin, DryRun: false));

        outcome.Success.Should().BeFalse("the shell could only have deleted it permanently");
        outcome.BytesFreed.Should().Be(0);
        outcome.Error.Should().Be(FileDeleter.RecycleBinUnavailableError);
        File.Exists(local).Should().BeTrue("Recycle Bin mode must not destroy the file");
    }

    [Fact]
    public async Task DeleteManyAsync_RecycleBatchWhereThereIsNoRecycleBin_RefusesEveryItem()
    {
        var first = CreateFile("no-bin-1.txt");
        var second = CreateFile("no-bin-2.txt");
        if (!TryGetLoopbackSharePath(first, out var firstNetwork) ||
            !TryGetLoopbackSharePath(second, out var secondNetwork))
        {
            return;
        }

        var outcomes = await CollectAsync(_deleter.DeleteManyAsync(
        [
            new DeletionRequest(firstNetwork, DeletionMethod.RecycleBin, DryRun: false),
            new DeletionRequest(secondNetwork, DeletionMethod.RecycleBin, DryRun: false),
        ]));

        // The refusal stops the whole shell batch; each item is then retried on
        // its own and refused there too.
        outcomes.Should().HaveCount(2);
        outcomes.Should().OnlyContain(o => !o.Success && o.Error == FileDeleter.RecycleBinUnavailableError);
        outcomes.Select(o => o.Path).Should().BeEquivalentTo([firstNetwork, secondNetwork]);
        File.Exists(first).Should().BeTrue();
        File.Exists(second).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteManyAsync_MixedBatchWithItemWithoutRecycleBin_RecyclesLocalItemAndRefusesTheRest()
    {
        // The shell decides "recycle or destroy" for a whole batch: before the guard,
        // one network item in the batch made it permanently delete the local item too.
        // This test really recycles one tiny temp file, as the existing batch tests do.
        var recyclable = CreateFile($"storagemaster-test-recycle-{Guid.NewGuid():N}.txt");
        var first = CreateFile("no-bin-1.txt");
        var second = CreateFile("no-bin-2.txt");
        if (!TryGetLoopbackSharePath(first, out var firstNetwork) ||
            !TryGetLoopbackSharePath(second, out var secondNetwork))
        {
            return;
        }

        var logger = new RecordingLogger();
        var deleter = new FileDeleter(logger, new FileSnapshotProvider());

        var outcomes = await CollectAsync(deleter.DeleteManyAsync(
        [
            new DeletionRequest(recyclable, DeletionMethod.RecycleBin, DryRun: false),
            new DeletionRequest(firstNetwork, DeletionMethod.RecycleBin, DryRun: false),
            new DeletionRequest(secondNetwork, DeletionMethod.RecycleBin, DryRun: false),
        ]));

        outcomes.Should().HaveCount(3);
        outcomes.Should().ContainSingle(o => o.Path == recyclable).Which.Success.Should().BeTrue(
            "the local file can be recycled on its own once the batch has been refused");
        outcomes.Where(o => o.Path != recyclable).Should().OnlyContain(
            o => !o.Success && o.Error == FileDeleter.RecycleBinUnavailableError);
        File.Exists(recyclable).Should().BeFalse();
        File.Exists(first).Should().BeTrue();
        File.Exists(second).Should().BeTrue();
        logger.Errors.Should().BeEmpty(
            "the shell must have put the local file in the Recycle Bin rather than destroying it");
    }

    [Fact]
    public async Task DeleteManyAsync_RecycleBatchOfLocalFiles_RecyclesEveryItemInOneBatch()
    {
        // The guard must not get in the way of an ordinary batch. Recycles two tiny temp files.
        var first = CreateFile($"storagemaster-test-recycle-{Guid.NewGuid():N}.txt");
        var second = CreateFile($"storagemaster-test-recycle-{Guid.NewGuid():N}.txt");
        var logger = new RecordingLogger();
        var deleter = new FileDeleter(logger, new FileSnapshotProvider());

        var outcomes = await CollectAsync(deleter.DeleteManyAsync(
        [
            new DeletionRequest(first, DeletionMethod.RecycleBin, DryRun: false),
            new DeletionRequest(second, DeletionMethod.RecycleBin, DryRun: false),
        ]));

        outcomes.Should().HaveCount(2);
        outcomes.Should().OnlyContain(o => o.Success && o.Error == null);
        outcomes.Sum(o => o.BytesFreed).Should().Be(2 * "recycle guard fixture".Length);
        File.Exists(first).Should().BeFalse();
        File.Exists(second).Should().BeFalse();
        logger.Messages.Should().NotContain(
            m => m.Contains("stopped early", StringComparison.Ordinal),
            "a batch the shell can recycle completely must not fall back to per-file mode");
        logger.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteManyAsync_GuardedBatchWithLockedFile_ReportsRecycledItemsAsRecycled()
    {
        // With the guard's progress sink attached, the shell skips a locked file,
        // recycles the rest and then reports the batch as aborted (without a sink it
        // reports success). Items it did recycle must still count as recycled — a
        // per-file retry of them would fail its snapshot re-check and report them as
        // "changed or replaced" — and only the locked file may fail.
        // Recycles one tiny temp file.
        var locked = CreateFile("locked-in-batch.txt");
        var recyclable = CreateFile($"storagemaster-test-recycle-{Guid.NewGuid():N}.txt");
        var snapshots = new FileSnapshotProvider();
        var lockedSnapshot = await snapshots.TakeSnapshotAsync(locked);
        var recyclableSnapshot = await snapshots.TakeSnapshotAsync(recyclable);

        List<DeletionOutcome> outcomes;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcomes = await CollectAsync(_deleter.DeleteManyAsync(
            [
                new DeletionRequest(locked, DeletionMethod.RecycleBin, DryRun: false, ExpectedSnapshot: lockedSnapshot),
                new DeletionRequest(recyclable, DeletionMethod.RecycleBin, DryRun: false, ExpectedSnapshot: recyclableSnapshot),
            ]));
        }

        outcomes.Should().HaveCount(2);
        outcomes.Should().ContainSingle(o => o.Path == recyclable).Which.Success.Should().BeTrue(
            "the file is in the Recycle Bin, so reporting it as changed or failed is wrong");
        outcomes.Should().ContainSingle(o => o.Path == locked).Which.Success.Should().BeFalse();
        File.Exists(recyclable).Should().BeFalse();
        File.Exists(locked).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteAsync_RecycleBinLockedFile_ReportsFailureInsteadOfSuccess()
    {
        var local = CreateFile("locked.txt");

        DeletionOutcome outcome;
        using (new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The progress sink now changes how a one-item run is judged, so pin
            // that an item the shell could not move still fails, whichever way
            // the shell reports it.
            outcome = await _deleter.DeleteAsync(
                new DeletionRequest(local, DeletionMethod.RecycleBin, DryRun: false));
        }

        outcome.Success.Should().BeFalse();
        outcome.BytesFreed.Should().Be(0);
        File.Exists(local).Should().BeTrue();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "recycle guard fixture");
        return path;
    }

    /// <summary>
    /// Maps C:\... to \\localhost\C$\... when that administrative share can see the file.
    /// </summary>
    private static bool TryGetLoopbackSharePath(string localPath, out string networkPath)
    {
        networkPath = string.Empty;
        var full = Path.GetFullPath(localPath);
        if (full.Length < 3 || full[1] != ':' || full[2] != Path.DirectorySeparatorChar)
            return false;

        var candidate = $@"\\localhost\{char.ToUpperInvariant(full[0])}${full[2..]}";
        try
        {
            if (!File.Exists(candidate))
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        networkPath = candidate;
        return true;
    }

    private sealed class RecordingLogger : ILogger<FileDeleter>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_entries) return _entries.Select(e => e.Message).ToArray(); }
        }

        public IReadOnlyList<string> Errors
        {
            get { lock (_entries) return _entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static async Task<List<DeletionOutcome>> CollectAsync(IAsyncEnumerable<DeletionOutcome> source)
    {
        var outcomes = new List<DeletionOutcome>();
        await foreach (var outcome in source)
            outcomes.Add(outcome);
        return outcomes;
    }
}
