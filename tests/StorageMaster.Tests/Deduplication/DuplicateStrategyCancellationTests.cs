using FluentAssertions;
using Moq;
using StorageMaster.Core.Deduplication;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Deduplication;

public sealed class DuplicateStrategyCancellationTests
{
    [Fact]
    public async Task ExactSha256_Cancellation_IsNotConvertedToHashError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hasher = new Mock<IFileContentHasher>();
        var snapshots = new Mock<IFileSnapshotProvider>();
        var candidate = MakeCandidate(@"C:\data\sample.bin", ".bin");
        snapshots.Setup(provider => provider.TakeSnapshotAsync(candidate.File.FullPath, cancellation.Token))
            .ReturnsAsync(new FileSnapshot(
                candidate.File.FullPath,
                Identity: null,
                candidate.File.SizeBytes,
                candidate.File.ModifiedUtc,
                candidate.File.Attributes));
        hasher.Setup(provider => provider.ComputeSha256Async(candidate.File.FullPath, cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var strategy = new ExactSha256Strategy(hasher.Object, snapshots.Object);

        Func<Task> act = () => strategy.ComputeSignatureAsync(candidate, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NormalizedText_Cancellation_IsNotConvertedToNormalizationError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"StorageMaster-cancel-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "content");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var strategy = new NormalizedTextStrategy();
        var candidate = MakeCandidate(path, ".txt");

        Func<Task> act = () => strategy.ComputeSignatureAsync(candidate, cancellation.Token);

        try
        {
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImagePHash_Cancellation_IsNotConvertedToPHashError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var strategy = new ImagePHashStrategy();
        var candidate = MakeCandidate(@"C:\data\sample.png", ".png");

        Func<Task> act = () => strategy.ComputeSignatureAsync(candidate, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static DuplicateCandidate MakeCandidate(string path, string extension) =>
        new(new FileEntry
        {
            Id = 1,
            SessionId = 2,
            FullPath = path,
            FileName = Path.GetFileName(path),
            Extension = extension,
            SizeBytes = 128,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow,
            AccessedUtc = DateTime.UtcNow,
            Attributes = FileAttributes.Normal,
            Category = FileTypeCategory.Unknown,
        });
}
