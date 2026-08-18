using System.Diagnostics;
using FluentAssertions;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.CriticalFixes;

public sealed class NoFollowFileEnumeratorTests
{
    [Fact]
    public async Task EnumerateAsync_NormalTree_ReturnsIdentitySnapshots()
    {
        var root = CreateTempDirectory("enumerate");
        var first = Path.Combine(root, "first.txt");
        var nested = Path.Combine(root, "nested");
        var second = Path.Combine(nested, "second.bin");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllBytesAsync(second, [1, 2, 3, 4]);

        try
        {
            var sut = new NoFollowFileEnumerator(new FileSnapshotProvider());

            var result = await sut.EnumerateAsync(root);

            result.IsPartial.Should().BeFalse();
            result.Errors.Should().BeEmpty();
            result.Files.Select(snapshot => snapshot.Path).Should().BeEquivalentTo(first, second);
            result.Files.Should().OnlyContain(snapshot => snapshot.Identity != null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateAsync_LocalAppDataTempBoundary_StillReturnsUserCacheFiles()
    {
        var tempBoundary = Path.GetFullPath(Path.GetTempPath());
        var cacheRoot = Path.Combine(tempBoundary, $"sm_nofollow_localappdata_{Guid.NewGuid():N}", "cache");
        var file = Path.Combine(cacheRoot, "candidate.tmp");
        Directory.CreateDirectory(cacheRoot);
        await File.WriteAllTextAsync(file, "candidate");

        try
        {
            var sut = new NoFollowFileEnumerator(new FileSnapshotProvider());

            var result = await sut.EnumerateAsync(tempBoundary, cacheRoot);

            result.Files.Select(snapshot => snapshot.Path).Should().ContainSingle()
                .Which.Should().Be(file);
            result.Errors.Should().NotContain(error =>
                error.Kind == NoFollowFileEnumerationErrorKind.AccessDenied ||
                error.Kind == NoFollowFileEnumerationErrorKind.EnumerationFailed);
            if (result.IsPartial)
            {
                result.Errors.Should().OnlyContain(error =>
                    error.Kind == NoFollowFileEnumerationErrorKind.ReplacementGuardUnavailable);
            }
        }
        finally
        {
            var testRoot = Directory.GetParent(cacheRoot)!.FullName;
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateAsync_Junction_SkipsTargetWithoutPartialError()
    {
        var testRoot = CreateTempDirectory("junction");
        var scanRoot = Path.Combine(testRoot, "scan");
        var target = Path.Combine(testRoot, "outside-target");
        var junction = Path.Combine(scanRoot, "linked");
        var local = Path.Combine(scanRoot, "local.txt");
        var sentinel = Path.Combine(target, "must-not-enumerate.txt");
        Directory.CreateDirectory(scanRoot);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(local, "local");
        await File.WriteAllTextAsync(sentinel, "sentinel");

        try
        {
            CreateJunction(junction, target).Should().BeTrue(
                "the Windows junction safety regression must exercise an actual junction");

            var sut = new NoFollowFileEnumerator(new FileSnapshotProvider());

            var result = await sut.EnumerateAsync(scanRoot);

            result.IsPartial.Should().BeFalse("reparse entries are intentional no-follow skips");
            result.Errors.Should().BeEmpty();
            result.Files.Select(snapshot => snapshot.Path).Should().Equal(local);
            result.Files.Should().NotContain(snapshot =>
                string.Equals(snapshot.Path, sentinel, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteLinkIfPresent(junction);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateAsync_BoundaryAncestorIsJunction_DoesNotReachScanRootTarget()
    {
        var testRoot = CreateTempDirectory("boundary-junction");
        var boundary = Path.Combine(testRoot, "boundary");
        var target = Path.Combine(testRoot, "outside-target");
        var targetScanRoot = Path.Combine(target, "cache");
        var junction = Path.Combine(boundary, "profile");
        var lexicalScanRoot = Path.Combine(junction, "cache");
        var sentinel = Path.Combine(targetScanRoot, "must-not-enumerate.txt");
        Directory.CreateDirectory(boundary);
        Directory.CreateDirectory(targetScanRoot);
        await File.WriteAllTextAsync(sentinel, "sentinel");

        try
        {
            CreateJunction(junction, target).Should().BeTrue(
                "the boundary regression must exercise an actual ancestor junction");

            var sut = new NoFollowFileEnumerator(new FileSnapshotProvider());

            var result = await sut.EnumerateAsync(boundary, lexicalScanRoot);

            result.IsPartial.Should().BeTrue(
                "a required boundary-to-scan-root reparse makes the requested scan incomplete");
            result.Errors.Should().ContainSingle(error =>
                error.Kind == NoFollowFileEnumerationErrorKind.InspectionFailed &&
                string.Equals(error.Path, junction, StringComparison.OrdinalIgnoreCase));
            result.Files.Should().BeEmpty();
            File.Exists(sentinel).Should().BeTrue();
        }
        finally
        {
            DeleteLinkIfPresent(junction);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateAsync_QueuedDirectoryBecomesJunction_FailsClosed()
    {
        var testRoot = CreateTempDirectory("swap");
        var scanRoot = Path.Combine(testRoot, "scan");
        var child = Path.Combine(scanRoot, "candidate");
        var target = Path.Combine(testRoot, "outside-target");
        var probe = Path.Combine(testRoot, "junction-probe");
        var local = Path.Combine(scanRoot, "local.txt");
        var sentinel = Path.Combine(target, "must-survive.txt");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(child, "replace-me.txt"), "replace");
        await File.WriteAllTextAsync(local, "local");
        await File.WriteAllTextAsync(sentinel, "sentinel");

        try
        {
            CreateJunction(probe, target).Should().BeTrue(
                "the swap regression must first prove junction creation is available");
            DeleteLinkIfPresent(probe);

            var swapped = false;
            var sut = new NoFollowFileEnumerator(
                new FileSnapshotProvider(),
                directory =>
                {
                    if (swapped || !string.Equals(directory, child, StringComparison.OrdinalIgnoreCase))
                        return;

                    Directory.Delete(child, recursive: true);
                    if (!CreateJunction(child, target))
                        throw new IOException("Test could not replace the queued directory with a junction.");
                    swapped = true;
                });

            var result = await sut.EnumerateAsync(scanRoot);

            swapped.Should().BeTrue("the race seam must replace the child before its guarded open");
            result.Errors.Should().BeEmpty("the replacement reparse entry is an intentional skip");
            result.Files.Select(snapshot => snapshot.Path).Should().Equal(local);
            File.Exists(sentinel).Should().BeTrue();
        }
        finally
        {
            DeleteLinkIfPresent(probe);
            DeleteLinkIfPresent(child);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateAsync_SnapshotUnavailable_ReturnsOtherFilesAndExplicitError()
    {
        var root = CreateTempDirectory("partial");
        var available = Path.Combine(root, "available.txt");
        var unavailable = Path.Combine(root, "unavailable.txt");
        await File.WriteAllTextAsync(available, "available");
        await File.WriteAllTextAsync(unavailable, "unavailable");

        try
        {
            var provider = new SelectivelyUnavailableSnapshotProvider(unavailable);
            var sut = new NoFollowFileEnumerator(provider);

            var result = await sut.EnumerateAsync(root);

            result.IsPartial.Should().BeTrue();
            result.Files.Select(snapshot => snapshot.Path).Should().Equal(available);
            result.Errors.Should().ContainSingle(error =>
                error.Kind == NoFollowFileEnumerationErrorKind.SnapshotUnavailable &&
                string.Equals(error.Path, unavailable, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateAsync_IdentityUnavailable_OmitsFileAndReturnsExplicitError()
    {
        var root = CreateTempDirectory("identity-partial");
        var file = Path.Combine(root, "no-identity.txt");
        await File.WriteAllTextAsync(file, "identity required");

        try
        {
            var sut = new NoFollowFileEnumerator(new IdentityRemovingSnapshotProvider());

            var result = await sut.EnumerateAsync(root);

            result.Files.Should().BeEmpty("files without a stable identity must fail closed");
            result.IsPartial.Should().BeTrue();
            result.Errors.Should().ContainSingle(error =>
                error.Kind == NoFollowFileEnumerationErrorKind.IdentityUnavailable &&
                string.Equals(error.Path, file, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateAsync_MissingRoot_ReturnsStructuredPartialError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"sm_missing_{Guid.NewGuid():N}");
        var sut = new NoFollowFileEnumerator(new FileSnapshotProvider());

        var result = await sut.EnumerateAsync(missing);

        result.Files.Should().BeEmpty();
        result.IsPartial.Should().BeTrue();
        result.Errors.Should().ContainSingle(error =>
            error.Kind == NoFollowFileEnumerationErrorKind.NotFound &&
            string.Equals(error.Path, missing, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnumerateAsync_Cancelled_PropagatesCancellation()
    {
        var root = CreateTempDirectory("cancel");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var sut = new NoFollowFileEnumerator(new FileSnapshotProvider());

            var act = () => sut.EnumerateAsync(root, cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ValidationLease_WhileHeld_BlocksAncestorRenameButAllowsChildDelete()
    {
        var testRoot = CreateTempDirectory("lease");
        var root = Path.Combine(testRoot, "root");
        var nested = Path.Combine(root, "nested");
        var file = Path.Combine(nested, "delete-me.txt");
        var moved = Path.Combine(testRoot, "moved");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(file, "delete");

        try
        {
            var snapshots = new FileSnapshotProvider();
            var expected = await snapshots.TakeSnapshotAsync(file);
            expected.Should().NotBeNull();
            expected!.Identity.Should().NotBeNull();
            var sut = new NoFollowFileEnumerator(snapshots);

            using (var lease = await sut.TryOpenValidatedFileAsync(root, expected))
            {
                lease.Should().NotBeNull();
                lease!.LiveSnapshot.IsIdenticalTo(expected).Should().BeTrue();

                var rename = () => Directory.Move(root, moved);
                rename.Should().Throw<IOException>(
                    "the lease must keep every guarded ancestor bound");

                var delete = () => File.Delete(file);
                delete.Should().NotThrow(
                    "directory guards must not block deletion of the validated child file");
            }

            Directory.Move(root, moved);
            Directory.Exists(moved).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task WeakReadGuard_AnalysisIsExplicitlyPartialAndValidationFailsClosed()
    {
        var root = CreateTempDirectory("weak-guard");
        var file = Path.Combine(root, "candidate.tmp");
        await File.WriteAllTextAsync(file, "candidate");

        try
        {
            var snapshots = new FileSnapshotProvider();
            var expected = await snapshots.TakeSnapshotAsync(file);
            expected.Should().NotBeNull();
            var sut = new NoFollowFileEnumerator(
                snapshots,
                beforeDirectoryGuardOpen: null,
                forceWeakReadGuards: true);

            var result = await sut.EnumerateAsync(root);
            using var lease = await sut.TryOpenValidatedFileAsync(root, expected!);

            result.Files.Select(snapshot => snapshot.Path).Should().Equal(file);
            result.IsPartial.Should().BeTrue();
            result.Errors.Should().ContainSingle(error =>
                error.Kind == NoFollowFileEnumerationErrorKind.ReplacementGuardUnavailable &&
                string.Equals(error.Path, root, StringComparison.OrdinalIgnoreCase));
            lease.Should().BeNull(
                "weak analysis guards must never authorize a later path-based deletion");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ValidationLease_FileOutsideRoot_ReturnsNull()
    {
        var testRoot = CreateTempDirectory("lease-containment");
        var root = Path.Combine(testRoot, "root");
        var outside = Path.Combine(testRoot, "outside.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(outside, "outside");

        try
        {
            var snapshots = new FileSnapshotProvider();
            var expected = await snapshots.TakeSnapshotAsync(outside);
            expected.Should().NotBeNull();
            var sut = new NoFollowFileEnumerator(snapshots);

            using var lease = await sut.TryOpenValidatedFileAsync(root, expected!);

            lease.Should().BeNull();
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm_nofollow_{suffix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool CreateJunction(string junction, string target)
    {
        try
        {
            var startInfo = new ProcessStartInfo(
                "cmd.exe",
                $"/d /c mklink /J \"{junction}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(5000))
                return false;

            return process.ExitCode == 0 && Directory.Exists(junction);
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteLinkIfPresent(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: false);
    }

    private sealed class SelectivelyUnavailableSnapshotProvider(string unavailablePath)
        : IFileSnapshotProvider
    {
        private readonly FileSnapshotProvider _inner = new();

        public ValueTask<FileSnapshot?> TakeSnapshotAsync(
            string path,
            CancellationToken ct = default) =>
            string.Equals(path, unavailablePath, StringComparison.OrdinalIgnoreCase)
                ? ValueTask.FromResult<FileSnapshot?>(null)
                : _inner.TakeSnapshotAsync(path, ct);
    }

    private sealed class IdentityRemovingSnapshotProvider : IFileSnapshotProvider
    {
        private readonly FileSnapshotProvider _inner = new();

        public async ValueTask<FileSnapshot?> TakeSnapshotAsync(
            string path,
            CancellationToken ct = default)
        {
            var snapshot = await _inner.TakeSnapshotAsync(path, ct);
            return snapshot is null ? null : snapshot with { Identity = null };
        }
    }

}
