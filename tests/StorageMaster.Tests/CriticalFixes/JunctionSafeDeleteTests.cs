using System.Runtime.InteropServices;
using FluentAssertions;
using StorageMaster.Platform.Windows;
using StorageMaster.Platform.Windows.Interop;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// C7: Verifies that DeletePermanently does NOT follow junctions/symlinks
/// into their targets. Only the link itself should be removed.
/// </summary>
public sealed class JunctionSafeDeleteTests
{
    [Fact]
    public void DeletePermanently_Junction_RemovesLinkOnly_DoesNotDeleteTarget()
    {
        // Skip on environments where junction creation fails (e.g. non-admin CI).
        var root = Path.Combine(Path.GetTempPath(), $"c7_{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "precious.txt"), "do not delete");

        try
        {
            // CreateJunction via cmd /c mklink /J — works without elevation.
            var result = CreateJunction(link, target);
            if (!result)
            {
                // Can't create junction — skip test gracefully.
                return;
            }

            // Precondition: junction exists and points to target.
            Directory.Exists(link).Should().BeTrue("junction should exist before delete");
            var attrs = File.GetAttributes(link);
            (attrs & FileAttributes.ReparsePoint).Should().NotBe(0, "link should be a reparse point");
            using (var guard = DirectoryTraversalInterop.TryOpenNoFollow(link))
            {
                guard.Should().NotBeNull();
                guard!.IsReparsePoint.Should().BeTrue();
                guard.ReparseTag.Should().NotBe(0);
            }

            // Act: delete the junction with our safe method.
            FileDeleter.DeletePermanently(link);

            // Assert: junction is gone, target is intact.
            Directory.Exists(link).Should().BeFalse("junction should be removed");
            Directory.Exists(target).Should().BeTrue("target directory must survive");
            File.Exists(Path.Combine(target, "precious.txt")).Should().BeTrue(
                "files inside the junction target must NOT be deleted");
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeletePermanently_DirContainingJunction_RemovesLinkButKeepsTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"c7b_{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var container = Path.Combine(root, "container");
        var link = Path.Combine(container, "junction_inside");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(container);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep me");
        File.WriteAllText(Path.Combine(container, "normal.txt"), "delete me");

        try
        {
            if (!CreateJunction(link, target))
                return;

            // Act: delete the container — should remove normal.txt and the junction,
            // but NOT follow the junction into target.
            FileDeleter.DeletePermanently(container);

            Directory.Exists(container).Should().BeFalse("container should be deleted");
            Directory.Exists(target).Should().BeTrue("target must survive");
            File.Exists(Path.Combine(target, "keep.txt")).Should().BeTrue(
                "target content must be untouched");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeletePermanently_NormalDirectory_DeletesRecursively()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"c7c_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "file.txt"), "data");

        FileDeleter.DeletePermanently(dir);

        Directory.Exists(dir).Should().BeFalse("normal directories should be recursively deleted");
    }

    [Fact]
    public void DeletePermanently_SingleFile_DeletesFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"c7d_{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "data");

        FileDeleter.DeletePermanently(file);

        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public void DirectoryTraversalGuard_WhileHeld_BlocksDirectoryRename()
    {
        var root = Path.Combine(Path.GetTempPath(), $"c7_guard_{Guid.NewGuid():N}");
        var guarded = Path.Combine(root, "guarded");
        var moved = Path.Combine(root, "moved");
        Directory.CreateDirectory(guarded);

        try
        {
            using (var guard = DirectoryTraversalInterop.TryOpenNoFollow(guarded))
            {
                guard.Should().NotBeNull();
                guard!.IsReparsePoint.Should().BeFalse();

                var rename = () => Directory.Move(guarded, moved);
                rename.Should().Throw<IOException>(
                    "the no-delete-share handle must keep the guarded name bound to the inspected directory");
            }

            Directory.Move(guarded, moved);
            Directory.Exists(moved).Should().BeTrue(
                "rename should work again after the traversal guard is released");
        }
        finally
        {
            if (Directory.Exists(guarded)) Directory.Delete(guarded, recursive: true);
            if (Directory.Exists(moved)) Directory.Delete(moved, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectoryTraversalGuard_WhileHeld_BlocksAncestorRename()
    {
        var root = Path.Combine(Path.GetTempPath(), $"c7_ancestor_{Guid.NewGuid():N}");
        var ancestor = Path.Combine(root, "ancestor");
        var guarded = Path.Combine(ancestor, "guarded");
        var movedAncestor = Path.Combine(root, "moved");
        Directory.CreateDirectory(guarded);

        try
        {
            using (var guard = DirectoryTraversalInterop.TryOpenNoFollow(guarded))
            {
                guard.Should().NotBeNull();

                var rename = () => Directory.Move(ancestor, movedAncestor);
                rename.Should().Throw<IOException>(
                    "an ancestor rename must not redirect path-based enumeration away from the guarded object");
            }

            Directory.Move(ancestor, movedAncestor);
            Directory.Exists(Path.Combine(movedAncestor, "guarded")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(ancestor)) Directory.Delete(ancestor, recursive: true);
            if (Directory.Exists(movedAncestor)) Directory.Delete(movedAncestor, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectoryTraversalGuard_MarkForDeletion_RemovesGuardedDirectoryOnDispose()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"c7_disposition_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            using (var guard = DirectoryTraversalInterop.TryOpenNoFollow(directory))
            {
                guard.Should().NotBeNull();
                guard!.IsReparsePoint.Should().BeFalse();
                guard.MarkForDeletion();
            }

            Directory.Exists(directory).Should().BeFalse(
                "the guarded object should be deleted without reopening its path");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeletePermanently_ChildBecomesJunctionBeforeNoFollowOpen_DoesNotTraverseTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"c7_swap_{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var container = Path.Combine(root, "container");
        var child = Path.Combine(container, "candidate");
        var probe = Path.Combine(root, "junction_probe");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(child);
        var precious = Path.Combine(target, "precious.txt");
        File.WriteAllText(precious, "must survive");
        File.WriteAllText(Path.Combine(child, "ordinary.txt"), "replace me");

        try
        {
            CreateJunction(probe, target).Should().BeTrue(
                "the deterministic swap test requires junction support");
            Directory.Delete(probe, recursive: false);

            var swapped = false;
            FileDeleter.DeletePermanently(container, directory =>
            {
                if (swapped || !string.Equals(directory, child, StringComparison.OrdinalIgnoreCase))
                    return;

                Directory.Delete(child, recursive: true);
                if (!CreateJunction(child, target))
                    throw new IOException("Test could not replace the child directory with a junction.");
                swapped = true;
            });

            swapped.Should().BeTrue("the race seam must replace the child before its guarded open");
            Directory.Exists(container).Should().BeFalse();
            File.Exists(precious).Should().BeTrue(
                "the no-follow handle must classify the replacement junction and refuse recursion");
        }
        finally
        {
            if (Directory.Exists(probe)) Directory.Delete(probe, recursive: false);
            if (Directory.Exists(child)) Directory.Delete(child, recursive: false);
            if (Directory.Exists(container)) Directory.Delete(container, recursive: true);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static bool CreateJunction(string junction, string target)
    {
        try
        {
            // Use Directory.CreateSymbolicLink on .NET 7+.
            // On older frameworks or without permissions, fall back to mklink /J.
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 && Directory.Exists(junction);
        }
        catch
        {
            return false;
        }
    }
}
