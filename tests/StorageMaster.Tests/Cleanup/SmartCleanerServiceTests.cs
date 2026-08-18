using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.SmartCleaner;

namespace StorageMaster.Tests.Cleanup;

public sealed class SmartCleanerServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_PartialEnumeration_ReturnsConfirmedFilesAndExactWarnings()
    {
        var path = LocalTempPath($"smclean_analysis_{Guid.NewGuid():N}.tmp");
        var snapshot = SnapshotFor(path, 1, sizeBytes: 7);
        var warning = new NoFollowFileEnumerationError(
            Path.Combine(Path.GetDirectoryName(path)!, "locked"),
            NoFollowFileEnumerationErrorKind.AccessDenied,
            "Access denied.");
        var localTemp = Path.GetFullPath(Path.GetDirectoryName(path)!);
        var enumerator = new Mock<INoFollowFileEnumerator>();
        enumerator.Setup(service => service.EnumerateAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, scanRoot, _) =>
                Task.FromResult(PathsEqual(scanRoot, localTemp)
                    ? new NoFollowFileEnumerationResult([snapshot], [warning])
                    : new NoFollowFileEnumerationResult([], [])));
        var service = CreateService(Mock.Of<IFileDeleter>(), enumerator.Object, CreateLog().Object);

        var result = await service.AnalyzeAsync();

        result.IsPartial.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().Be(warning);
        var group = result.Groups.Single(group => group.Source == SmartCleanSource.TemporaryFiles);
        group.Paths.Should().ContainSingle().Which.Should().Be(path);
        group.ExpectedFileSnapshots[path].Should().Be(snapshot);
        group.EstimatedBytes.Should().Be(7);
    }

    [Fact]
    public async Task CleanAsync_ValidatedFile_HoldsLeaseAndForwardsExactLiveSnapshot()
    {
        var path = LocalTempPath($"smclean_snapshot_{Guid.NewGuid():N}.tmp");
        var expected = SnapshotFor(path, 10, sizeBytes: 8);
        var live = expected with { LastWriteUtc = expected.LastWriteUtc.AddTicks(1) };
        var lease = new TrackingLease(live);
        var enumerator = CreateValidationEnumerator(_ => lease);
        DeletionRequest? captured = null;
        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(service => service.DeleteAsync(
                It.IsAny<DeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<DeletionRequest, CancellationToken>((request, _) =>
            {
                lease.IsDisposed.Should().BeFalse("ancestry must remain guarded through deletion");
                captured = request;
                return Task.FromResult(new DeletionOutcome(request.Path, true, request.ExpectedSnapshot!.SizeBytes));
            });
        var service = CreateService(deleter.Object, enumerator.Object, CreateLog().Object);

        var result = await service.CleanAsync(
            [GroupFor(expected)],
            DeletionMethod.RecycleBin);

        captured.Should().NotBeNull();
        captured!.ExpectedSnapshot.Should().BeSameAs(live);
        captured.Method.Should().Be(DeletionMethod.RecycleBin);
        lease.IsDisposed.Should().BeTrue();
        result.BytesProcessed.Should().Be(8);
        result.BytesFreed.Should().Be(0, "moving a file to the Recycle Bin does not reclaim disk space");
        result.IsFullySuccessful.Should().BeTrue();
    }

    [Fact]
    public async Task CleanAsync_PathOutsideKnownCategoryRoot_NeverReachesValidationOrDeletion()
    {
        var unsafePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $"smclean_unsafe_{Guid.NewGuid():N}.txt");
        var snapshot = SnapshotFor(unsafePath, 20);
        var enumerator = new Mock<INoFollowFileEnumerator>();
        var deleter = new Mock<IFileDeleter>();
        var service = CreateService(deleter.Object, enumerator.Object, CreateLog().Object);

        var result = await service.CleanAsync([GroupFor(snapshot)], DeletionMethod.RecycleBin);

        enumerator.Verify(service => service.TryOpenValidatedFileAsync(
            It.IsAny<string>(),
            It.IsAny<FileSnapshot>(),
            It.IsAny<CancellationToken>()), Times.Never);
        deleter.Verify(service => service.DeleteAsync(
            It.IsAny<DeletionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        result.Failures.Should().ContainSingle();
        result.AllDeletionsSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task CleanAsync_MissingStableValidationLease_FailsClosed()
    {
        var snapshot = SnapshotFor(LocalTempPath($"smclean_identity_{Guid.NewGuid():N}.tmp"), 30);
        var enumerator = CreateValidationEnumerator(_ => null);
        var deleter = new Mock<IFileDeleter>();
        var service = CreateService(deleter.Object, enumerator.Object, CreateLog().Object);

        var result = await service.CleanAsync([GroupFor(snapshot)], DeletionMethod.Permanent);

        deleter.Verify(service => service.DeleteAsync(
            It.IsAny<DeletionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        result.Failures.Should().ContainSingle()
            .Which.Error.Should().Contain("stable identity");
        result.BytesProcessed.Should().Be(0);
        result.BytesFreed.Should().Be(0);
    }

    [Fact]
    public async Task CleanAsync_MixedOutcomes_ReturnsAndAuditsPartialFailure()
    {
        var first = SnapshotFor(LocalTempPath($"smclean_partial_{Guid.NewGuid():N}_1.tmp"), 40, 5);
        var second = SnapshotFor(LocalTempPath($"smclean_partial_{Guid.NewGuid():N}_2.tmp"), 41, 6);
        var enumerator = CreateValidationEnumerator(expected => new TrackingLease(expected));
        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(service => service.DeleteAsync(
                It.IsAny<DeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<DeletionRequest, CancellationToken>((request, _) =>
                Task.FromResult(PathsEqual(request.Path, first.Path)
                    ? new DeletionOutcome(request.Path, true, BytesFreed: 500)
                    : new DeletionOutcome(request.Path, false, BytesFreed: 0, Error: "locked")));
        CleanupResult? audit = null;
        var log = CreateLog();
        log.Setup(repository => repository.LogResultAsync(
                It.IsAny<CleanupResult>(),
                It.IsAny<CleanupSuggestion>(),
                It.IsAny<CancellationToken>()))
            .Callback<CleanupResult, CleanupSuggestion, CancellationToken>((result, _, _) => audit = result)
            .Returns(Task.CompletedTask);
        var service = CreateService(deleter.Object, enumerator.Object, log.Object);

        var result = await service.CleanAsync(
            [GroupFor(first, second)],
            DeletionMethod.RecycleBin);

        result.BytesProcessed.Should().Be(5);
        result.BytesFreed.Should().Be(0);
        result.SuccessfulPathCount.Should().Be(1);
        result.Failures.Should().ContainSingle().Which.Path.Should().Be(second.Path);
        result.AllDeletionsSucceeded.Should().BeFalse();
        audit.Should().NotBeNull();
        audit!.Status.Should().Be(CleanupResultStatus.PartialSuccess);
        audit.BytesFreed.Should().Be(0);
        audit.FailedPaths.Should().ContainSingle().Which.Should().Be(second.Path);
    }

    [Fact]
    public async Task CleanAsync_PermanentOutcome_ClampsReportedFreedBytesToValidatedSize()
    {
        var snapshot = SnapshotFor(LocalTempPath($"smclean_clamp_{Guid.NewGuid():N}.tmp"), 50, 11);
        var enumerator = CreateValidationEnumerator(expected => new TrackingLease(expected));
        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(service => service.DeleteAsync(
                It.IsAny<DeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeletionRequest request, CancellationToken _) =>
                new DeletionOutcome(request.Path, true, BytesFreed: long.MaxValue));
        var service = CreateService(deleter.Object, enumerator.Object, CreateLog().Object);

        var result = await service.CleanAsync([GroupFor(snapshot)], DeletionMethod.Permanent);

        result.BytesProcessed.Should().Be(11);
        result.BytesFreed.Should().Be(11);
        result.IsFullySuccessful.Should().BeTrue();
    }

    [Fact]
    public async Task CleanAsync_AuditWriteFails_ReportsWarningWithoutRepeatingDeletion()
    {
        var snapshot = SnapshotFor(LocalTempPath($"smclean_audit_{Guid.NewGuid():N}.tmp"), 60, 4);
        var enumerator = CreateValidationEnumerator(expected => new TrackingLease(expected));
        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(service => service.DeleteAsync(
                It.IsAny<DeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeletionRequest request, CancellationToken _) =>
                new DeletionOutcome(request.Path, true, request.ExpectedSnapshot!.SizeBytes));
        var log = new Mock<ICleanupLogRepository>();
        log.Setup(repository => repository.LogResultAsync(
                It.IsAny<CleanupResult>(),
                It.IsAny<CleanupSuggestion>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database unavailable"));
        var service = CreateService(deleter.Object, enumerator.Object, log.Object);

        var result = await service.CleanAsync([GroupFor(snapshot)], DeletionMethod.RecycleBin);

        result.AllDeletionsSucceeded.Should().BeTrue();
        result.IsFullySuccessful.Should().BeFalse();
        result.AuditWarnings.Should().ContainSingle();
        deleter.Verify(service => service.DeleteAsync(
            It.IsAny<DeletionRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CleanAsync_CancellationAfterDeletion_ReturnsPartialResultAndDoesNotRetry()
    {
        var first = SnapshotFor(LocalTempPath($"smclean_cancel_{Guid.NewGuid():N}_1.tmp"), 70, 3);
        var second = SnapshotFor(LocalTempPath($"smclean_cancel_{Guid.NewGuid():N}_2.tmp"), 71, 4);
        var enumerator = CreateValidationEnumerator(expected => new TrackingLease(expected));
        using var cts = new CancellationTokenSource();
        var callCount = 0;
        var deleter = new Mock<IFileDeleter>();
        deleter.Setup(service => service.DeleteAsync(
                It.IsAny<DeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<DeletionRequest, CancellationToken>((request, _) =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult(new DeletionOutcome(request.Path, true, request.ExpectedSnapshot!.SizeBytes));

                cts.Cancel();
                return Task.FromCanceled<DeletionOutcome>(cts.Token);
            });
        var service = CreateService(deleter.Object, enumerator.Object, CreateLog().Object);

        var result = await service.CleanAsync(
            [GroupFor(first, second)],
            DeletionMethod.RecycleBin,
            ct: cts.Token);

        result.WasCancelled.Should().BeTrue();
        result.SuccessfulPathCount.Should().Be(1);
        result.BytesProcessed.Should().Be(3);
        result.BytesFreed.Should().Be(0);
        result.Failures.Should().ContainSingle().Which.Path.Should().Be(second.Path);
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task CleanAsync_QuarantineMethod_IsRejectedBeforeDeletion()
    {
        var snapshot = SnapshotFor(LocalTempPath($"smclean_method_{Guid.NewGuid():N}.tmp"), 80);
        var enumerator = CreateValidationEnumerator(expected => new TrackingLease(expected));
        var deleter = new Mock<IFileDeleter>();
        var service = CreateService(deleter.Object, enumerator.Object, CreateLog().Object);

        var result = await service.CleanAsync([GroupFor(snapshot)], DeletionMethod.Quarantine);

        result.Failures.Should().ContainSingle();
        deleter.Verify(service => service.DeleteAsync(
            It.IsAny<DeletionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SmartCleanerService CreateService(
        IFileDeleter deleter,
        INoFollowFileEnumerator enumerator,
        ICleanupLogRepository log) =>
        new(deleter, enumerator, log, NullLogger<SmartCleanerService>.Instance);

    private static Mock<INoFollowFileEnumerator> CreateValidationEnumerator(
        Func<FileSnapshot, INoFollowFileValidationLease?> factory)
    {
        var enumerator = new Mock<INoFollowFileEnumerator>();
        enumerator.Setup(service => service.TryOpenValidatedFileAsync(
                It.IsAny<string>(),
                It.IsAny<FileSnapshot>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, FileSnapshot, CancellationToken>((_, expected, _) =>
                ValueTask.FromResult(factory(expected)));
        return enumerator;
    }

    private static Mock<ICleanupLogRepository> CreateLog()
    {
        var log = new Mock<ICleanupLogRepository>();
        log.Setup(repository => repository.LogResultAsync(
                It.IsAny<CleanupResult>(),
                It.IsAny<CleanupSuggestion>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return log;
    }

    private static SmartCleanGroup GroupFor(params FileSnapshot[] snapshots) =>
        new(
            SmartCleanSource.TemporaryFiles,
            "Temporary Files",
            "test",
            string.Empty,
            snapshots.Sum(static snapshot => snapshot.SizeBytes),
            snapshots.Select(static snapshot => snapshot.Path).ToArray(),
            snapshots.ToDictionary(
                static snapshot => snapshot.Path,
                StringComparer.OrdinalIgnoreCase));

    private static FileSnapshot SnapshotFor(string path, ulong identity, long sizeBytes = 1) =>
        new(
            Path.GetFullPath(path),
            new FileIdentity("TESTVOL", identity),
            sizeBytes,
            new DateTime(638900000000000000 + (long)identity, DateTimeKind.Utc),
            FileAttributes.Archive);

    private static string LocalTempPath(string fileName)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp");
        Directory.CreateDirectory(root);
        return Path.GetFullPath(Path.Combine(root, fileName));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private sealed class TrackingLease(FileSnapshot liveSnapshot) : INoFollowFileValidationLease
    {
        public FileSnapshot LiveSnapshot { get; } = liveSnapshot;
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
