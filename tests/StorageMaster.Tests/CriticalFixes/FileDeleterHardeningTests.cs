using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.CriticalFixes;

public sealed class FileDeleterHardeningTests
{
    [Fact]
    public async Task DeleteAsync_PermanentReadOnlyFile_RemovesReadOnlyFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"readonly_{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "data");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var deleter = new FileDeleter(NullLogger<FileDeleter>.Instance);

        var outcome = await deleter.DeleteAsync(new DeletionRequest(
            file,
            DeletionMethod.Permanent,
            DryRun: false));

        outcome.Success.Should().BeTrue(outcome.Error);
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_DirectoryQuarantine_IsRejectedClearly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"quarantine_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var deleter = new FileDeleter(NullLogger<FileDeleter>.Instance);

        try
        {
            var outcome = await deleter.DeleteAsync(new DeletionRequest(
                dir,
                DeletionMethod.Quarantine,
                DryRun: false,
                QuarantineRunId: 123));

            outcome.Success.Should().BeFalse();
            outcome.Error.Should().Contain("Directory quarantine is not supported");
            Directory.Exists(dir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EstimateSize_CancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => FileDeleter.EstimateSize(Path.GetTempPath(), cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Theory]
    [InlineData(DeletionMethod.RecycleBin)]
    [InlineData(DeletionMethod.Quarantine)]
    public async Task DeleteAsync_EmptyRecycleBinSentinel_RequiresPermanentMode(DeletionMethod method)
    {
        var deleter = new FileDeleter(NullLogger<FileDeleter>.Instance);

        var outcome = await deleter.DeleteAsync(new DeletionRequest(
            "::RecycleBin::",
            method,
            DryRun: false));

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("requires Permanent deletion mode");
    }

    [Fact]
    public async Task DeleteAsync_ExpectedSnapshotDoesNotMatchReplacement_RefusesDeletion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snapshot_guard_{Guid.NewGuid():N}.txt");
        var snapshots = new FileSnapshotProvider();
        File.WriteAllText(path, "same");
        var expected = await snapshots.TakeSnapshotAsync(path);
        expected.Should().NotBeNull();

        try
        {
            File.Delete(path);
            File.WriteAllText(path, "evil");
            File.SetLastWriteTimeUtc(path, expected!.LastWriteUtc);
            var deleter = new FileDeleter(NullLogger<FileDeleter>.Instance, snapshots);

            var outcome = await deleter.DeleteAsync(new DeletionRequest(
                path,
                DeletionMethod.Permanent,
                DryRun: false,
                ExpectedSnapshot: expected));

            outcome.Success.Should().BeFalse();
            outcome.Error.Should().Contain("changed or was replaced");
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
