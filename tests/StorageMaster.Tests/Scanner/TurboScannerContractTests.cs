using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace StorageMaster.Tests.Scanner;

/// <summary>
/// End-to-end JSONL contract tests against the real turbo-scanner.exe built
/// from turbo-scanner/. The binary is located in the repo's cargo target
/// directory; when it has not been built locally (e.g. a machine without the
/// Rust toolchain), these tests no-op so CI without cargo stays green — the
/// Rust unit tests cover the same contract from the producing side.
/// </summary>
public sealed class TurboScannerContractTests
{
    private static string? FindBinary()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidateRoot = Path.Combine(dir.FullName, "turbo-scanner", "target");
            foreach (var profile in new[] { "release", "debug" })
            {
                var exe = Path.Combine(candidateRoot, profile, "turbo-scanner.exe");
                if (File.Exists(exe))
                    return exe;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static List<JsonElement> RunScanner(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(30_000).Should().BeTrue("turbo-scanner should finish quickly on a tiny fixture");
        process.ExitCode.Should().Be(0);

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
    }

    private static string CreateFixture(out string hiddenFile, out string hiddenDirFile)
    {
        var root = Path.Combine(Path.GetTempPath(), $"turbo_contract_{Guid.NewGuid():N}");
        var hiddenDir = Path.Combine(root, "hidden-dir");
        Directory.CreateDirectory(hiddenDir);
        File.WriteAllText(Path.Combine(root, "visible.txt"), "v");
        hiddenFile = Path.Combine(root, "hidden.txt");
        File.WriteAllText(hiddenFile, "h");
        hiddenDirFile = Path.Combine(hiddenDir, "inside.txt");
        File.WriteAllText(hiddenDirFile, "i");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        File.SetAttributes(hiddenDir, File.GetAttributes(hiddenDir) | FileAttributes.Hidden);
        return root;
    }

    [Fact]
    public void Records_CarryContractV2Fields_IncludingIsHidden()
    {
        var exe = FindBinary();
        if (exe is null)
            return; // Rust binary not built on this machine.

        var root = CreateFixture(out var hiddenFile, out _);
        try
        {
            var records = RunScanner(exe, "--path", root, "--threads", "1");

            records.Should().NotBeEmpty();
            foreach (var record in records)
            {
                record.TryGetProperty("path", out _).Should().BeTrue();
                record.TryGetProperty("size", out _).Should().BeTrue();
                record.TryGetProperty("modified_unix", out _).Should().BeTrue();
                record.TryGetProperty("created_unix", out _).Should().BeTrue();
                record.TryGetProperty("is_dir", out _).Should().BeTrue();
                record.TryGetProperty("is_hidden", out _).Should().BeTrue("contract v2 adds is_hidden");
            }

            var hidden = records.Single(r => r.GetProperty("path").GetString() == hiddenFile);
            hidden.GetProperty("is_hidden").GetBoolean().Should().BeTrue();

            var visible = records.Single(r => r.GetProperty("path").GetString()!.EndsWith("visible.txt"));
            visible.GetProperty("is_hidden").GetBoolean().Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SkipHidden_PrunesHiddenFilesAndHiddenDirectorySubtrees()
    {
        var exe = FindBinary();
        if (exe is null)
            return; // Rust binary not built on this machine.

        var root = CreateFixture(out var hiddenFile, out var hiddenDirFile);
        try
        {
            var paths = RunScanner(exe, "--path", root, "--threads", "1", "--skip-hidden")
                .Select(r => r.GetProperty("path").GetString())
                .ToList();

            paths.Should().Contain(p => p!.EndsWith("visible.txt"));
            paths.Should().NotContain(hiddenFile, "hidden files must be pruned");
            paths.Should().NotContain(hiddenDirFile, "files inside hidden directories must be pruned, matching the managed scanner");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
