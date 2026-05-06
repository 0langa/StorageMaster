using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Interfaces;
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
}
