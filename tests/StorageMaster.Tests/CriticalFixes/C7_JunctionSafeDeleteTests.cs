using System.Runtime.InteropServices;
using FluentAssertions;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// C7: Verifies that DeletePermanently does NOT follow junctions/symlinks
/// into their targets. Only the link itself should be removed.
/// </summary>
public sealed class C7_JunctionSafeDeleteTests
{
    [Fact]
    public void DeletePermanently_Junction_RemovesLinkOnly_DoesNotDeleteTarget()
    {
        // Skip on environments where junction creation fails (e.g. non-admin CI).
        var root   = Path.Combine(Path.GetTempPath(), $"c7_{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link   = Path.Combine(root, "junction");
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
        var root      = Path.Combine(Path.GetTempPath(), $"c7b_{Guid.NewGuid():N}");
        var target    = Path.Combine(root, "target");
        var container = Path.Combine(root, "container");
        var link      = Path.Combine(container, "junction_inside");
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

    private static bool CreateJunction(string junction, string target)
    {
        try
        {
            // Use Directory.CreateSymbolicLink on .NET 7+.
            // On older frameworks or without permissions, fall back to mklink /J.
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
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
