using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// Snapshot-guarded Recycle Bin requests now take the batch path instead of one
/// IFileOperation per file. These tests pin the property that made that safe:
/// every request is snapshot-verified BEFORE anything is submitted to the shell,
/// so batching changed how verified deletions are handed over, not whether they
/// are verified.
/// </summary>
public sealed class FileDeleterBatchGuardTests
{
    [Fact]
    public async Task DeleteManyAsync_GuardedRecycleBatch_RefusesEveryChangedFileAndDeletesNothing()
    {
        var directory = CreateTempDirectory();
        try
        {
            var snapshots = new FileSnapshotProvider();
            var requests = new List<DeletionRequest>();

            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(directory, $"guarded_{i}.txt");
                File.WriteAllText(path, "original");
                var expected = await snapshots.TakeSnapshotAsync(path);
                expected.Should().NotBeNull();

                // Replace the content so the live snapshot can no longer match.
                File.WriteAllText(path, "replaced with different content");

                requests.Add(new DeletionRequest(
                    path,
                    DeletionMethod.RecycleBin,
                    DryRun: false,
                    ExpectedSnapshot: expected));
            }

            var deleter = new FileDeleter(NullLogger<FileDeleter>.Instance, snapshots);

            var outcomes = new List<DeletionOutcome>();
            await foreach (var outcome in deleter.DeleteManyAsync(requests))
                outcomes.Add(outcome);

            outcomes.Should().HaveCount(3);
            outcomes.Should().OnlyContain(o => !o.Success);
            outcomes.Should().OnlyContain(o => o.Error!.Contains("changed or was replaced"));

            // Nothing reached the shell: every path is still where it was.
            foreach (var request in requests)
                File.Exists(request.Path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteManyAsync_GuardedRecycleBatch_ReVerifiesEverySnapshotBeforeSubmitting()
    {
        var directory = CreateTempDirectory();
        try
        {
            var paths = new List<string>();
            var requests = new List<DeletionRequest>();
            for (var i = 0; i < 4; i++)
            {
                var path = Path.Combine(directory, $"counted_{i}.txt");
                File.WriteAllText(path, "content");
                paths.Add(path);
                requests.Add(new DeletionRequest(
                    path,
                    DeletionMethod.RecycleBin,
                    DryRun: false,
                    ExpectedSnapshot: new FileSnapshot(
                        path,
                        new FileIdentity("DEADBEEF", (ulong)(i + 1)),
                        SizeBytes: 7,
                        LastWriteUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        Attributes: FileAttributes.Normal)));
            }

            var snapshots = new CountingSnapshotProvider();
            var deleter = new FileDeleter(NullLogger<FileDeleter>.Instance, snapshots);

            var outcomes = new List<DeletionOutcome>();
            await foreach (var outcome in deleter.DeleteManyAsync(requests))
                outcomes.Add(outcome);

            // One re-check per request — the batch path must not verify a sample.
            snapshots.CheckedPaths.Should().BeEquivalentTo(paths);
            outcomes.Should().HaveCount(4);
            outcomes.Should().OnlyContain(o => !o.Success);
            foreach (var path in paths)
                File.Exists(path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteManyAsync_GuardedBatchWithoutSnapshotProvider_RefusesInsteadOfDeleting()
    {
        var directory = CreateTempDirectory();
        try
        {
            var requests = new List<DeletionRequest>();
            for (var i = 0; i < 2; i++)
            {
                var path = Path.Combine(directory, $"unguardable_{i}.txt");
                File.WriteAllText(path, "content");
                requests.Add(new DeletionRequest(
                    path,
                    DeletionMethod.RecycleBin,
                    DryRun: false,
                    ExpectedSnapshot: new FileSnapshot(
                        path,
                        Identity: null,
                        SizeBytes: 7,
                        LastWriteUtc: DateTime.UtcNow,
                        Attributes: FileAttributes.Normal)));
            }

            // No snapshot provider wired in: a guarded request cannot be honoured.
            var deleter = new FileDeleter(NullLogger<FileDeleter>.Instance);

            var outcomes = new List<DeletionOutcome>();
            await foreach (var outcome in deleter.DeleteManyAsync(requests))
                outcomes.Add(outcome);

            outcomes.Should().HaveCount(2);
            outcomes.Should().OnlyContain(o => !o.Success);
            outcomes.Should().OnlyContain(o => o.Error!.Contains("Snapshot-guarded deletion is unavailable"));
            foreach (var request in requests)
                File.Exists(request.Path).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EstimateSize_NestedDirectory_SumsEveryFileLength()
    {
        var directory = CreateTempDirectory();
        try
        {
            var nested = Path.Combine(directory, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(Path.Combine(directory, "a.bin"), new byte[64]);
            File.WriteAllBytes(Path.Combine(nested, "b.bin"), new byte[128]);
            File.WriteAllBytes(Path.Combine(nested, "c.bin"), new byte[256]);

            FileDeleter.EstimateSize(directory).Should().Be(64 + 128 + 256);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sm_batch_guard_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class CountingSnapshotProvider : IFileSnapshotProvider
    {
        private readonly List<string> _checkedPaths = [];

        public IReadOnlyList<string> CheckedPaths
        {
            get { lock (_checkedPaths) return _checkedPaths.ToArray(); }
        }

        public ValueTask<FileSnapshot?> TakeSnapshotAsync(string path, CancellationToken ct = default)
        {
            lock (_checkedPaths)
                _checkedPaths.Add(path);

            // Never matches the caller's expectation, so nothing is ever submitted.
            return ValueTask.FromResult<FileSnapshot?>(new FileSnapshot(
                path,
                new FileIdentity("FEEDFACE", 999),
                SizeBytes: 12345,
                LastWriteUtc: new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                Attributes: FileAttributes.Normal));
        }
    }
}
