using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using StorageMaster.Platform.Windows;

namespace StorageMaster.Tests.Scanner;

/// <summary>
/// End-to-end JSONL contract tests against the real turbo-scanner.exe built
/// from turbo-scanner/. The binary is located in the repo's cargo target
/// directory; when it has not been built locally (e.g. a machine without the
/// Rust toolchain), these tests no-op unless STORAGEMASTER_REQUIRE_TURBO_SCANNER
/// is true. Required runs fail loudly when the configured binary is absent.
/// </summary>
public sealed class TurboScannerContractTests
{
    private const string BinaryEnvironmentVariable = "STORAGEMASTER_TURBO_SCANNER_BINARY";
    private const string RequiredEnvironmentVariable = "STORAGEMASTER_REQUIRE_TURBO_SCANNER";

    private static string? FindBinary()
    {
        var configuredBinary = Environment.GetEnvironmentVariable(BinaryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredBinary))
        {
            File.Exists(configuredBinary).Should().BeTrue(
                $"{BinaryEnvironmentVariable} explicitly names the required native scanner");
            return configuredBinary;
        }

        var targetRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configuredTarget = Environment.GetEnvironmentVariable("CARGO_TARGET_DIR");
        if (!string.IsNullOrWhiteSpace(configuredTarget))
            targetRoots.Add(Path.GetFullPath(configuredTarget));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            targetRoots.Add(Path.Combine(dir.FullName, "turbo-scanner", "target"));
            dir = dir.Parent;
        }

        foreach (var targetRoot in targetRoots)
            foreach (var relativePath in new[]
                     {
                     @"release\turbo-scanner.exe",
                     @"debug\turbo-scanner.exe",
                     @"x86_64-pc-windows-msvc\release\turbo-scanner.exe",
                     @"x86_64-pc-windows-msvc\debug\turbo-scanner.exe",
                 })
            {
                var exe = Path.Combine(targetRoot, relativePath);
                if (File.Exists(exe))
                    return exe;
            }

        return null;
    }

    private static string? FindBinaryOrSkip()
    {
        var binary = FindBinary();
        if (binary is null && IsTrue(Environment.GetEnvironmentVariable(RequiredEnvironmentVariable)))
        {
            Assert.Fail(
                $"{RequiredEnvironmentVariable}=true but turbo-scanner.exe was not found. " +
                $"Set {BinaryEnvironmentVariable} or build the Rust scanner first.");
        }

        return binary;
    }

    private static ScannerResult RunScannerProcess(string exe, params string[] args)
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
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("turbo-scanner should finish quickly on a tiny fixture");
        }

        return new ScannerResult(
            process.ExitCode,
            stdoutTask.GetAwaiter().GetResult(),
            stderrTask.GetAwaiter().GetResult());
    }

    private static List<JsonElement> RunScanner(string exe, params string[] args)
    {
        var result = RunScannerProcess(exe, args);
        result.ExitCode.Should().Be(0, result.StandardError);

        return result.StandardOutput
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
    public void Records_CarryLosslessContractFields()
    {
        var exe = FindBinaryOrSkip();
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
                record.TryGetProperty("modified_utc_ticks", out _).Should().BeTrue(
                    "contract v3 preserves Windows 100-nanosecond timestamps");
                record.TryGetProperty("created_utc_ticks", out _).Should().BeTrue(
                    "contract v3 preserves Windows 100-nanosecond timestamps");
                record.TryGetProperty("attributes", out _).Should().BeTrue(
                    "contract v3 carries raw Windows file attributes");
                record.TryGetProperty("volume_serial", out _).Should().BeTrue(
                    "contract v3 carries nullable stable volume identity");
                record.TryGetProperty("file_index", out _).Should().BeTrue(
                    "contract v3 carries nullable stable file identity");
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
    public async Task Records_PreserveExactWindowsMetadataAndIdentity()
    {
        var exe = FindBinaryOrSkip();
        if (exe is null)
            return;

        var root = CreateFixture(out var hiddenFile, out _);
        try
        {
            var requestedModifiedUtc = new DateTime(2026, 8, 18, 11, 22, 33, DateTimeKind.Utc).AddTicks(7);
            var requestedCreatedUtc = requestedModifiedUtc.AddDays(-2).AddTicks(4);
            File.SetLastWriteTimeUtc(hiddenFile, requestedModifiedUtc);
            File.SetCreationTimeUtc(hiddenFile, requestedCreatedUtc);
            File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.ReadOnly);

            var expectedModifiedUtc = File.GetLastWriteTimeUtc(hiddenFile);
            var expectedCreatedUtc = File.GetCreationTimeUtc(hiddenFile);
            var expectedAttributes = File.GetAttributes(hiddenFile);
            var expectedIdentity = await new FileIdentityProvider().GetIdentityAsync(hiddenFile);

            var record = RunScanner(exe, "--path", root, "--threads", "1")
                .Single(r => r.GetProperty("path").GetString() == hiddenFile);

            record.GetProperty("modified_utc_ticks").GetInt64().Should().Be(expectedModifiedUtc.Ticks);
            record.GetProperty("created_utc_ticks").GetInt64().Should().Be(expectedCreatedUtc.Ticks);
            record.GetProperty("modified_unix").GetInt64().Should().Be(
                (expectedModifiedUtc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond);
            record.GetProperty("created_unix").GetInt64().Should().Be(
                (expectedCreatedUtc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond);
            record.GetProperty("attributes").GetUInt32().Should().Be((uint)expectedAttributes);
            expectedIdentity.Should().NotBeNull();
            record.GetProperty("volume_serial").GetUInt32().ToString("X8")
                .Should().Be(expectedIdentity!.VolumeSerial);
            record.GetProperty("file_index").GetUInt64().Should().Be(expectedIdentity.FileIndex);
        }
        finally
        {
            if (File.Exists(hiddenFile))
                File.SetAttributes(hiddenFile, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RootJunction_IsRejectedUnlessFollowLinksWasExplicitlyEnabled()
    {
        var exe = FindBinaryOrSkip();
        if (exe is null)
            return;

        var container = Path.Combine(Path.GetTempPath(), $"turbo_link_{Guid.NewGuid():N}");
        var target = Path.Combine(Path.GetTempPath(), $"turbo_target_{Guid.NewGuid():N}");
        var junction = Path.Combine(container, "root-link");
        Directory.CreateDirectory(container);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "outside.txt"), "outside");
        CreateJunction(junction, target).Should().BeTrue("junction support is required for this Windows contract test");

        try
        {
            var rejected = RunScannerProcess(exe, "--path", junction, "--threads", "1");
            rejected.ExitCode.Should().NotBe(0);
            rejected.StandardOutput.Should().BeEmpty();
            rejected.StandardError.Should().Contain("reparse point");

            var followed = RunScanner(
                exe,
                "--path", junction,
                "--threads", "1",
                "--follow-links");
            followed.Should().Contain(record =>
                record.GetProperty("path").GetString()!.EndsWith("outside.txt"));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
            Directory.Delete(container, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void DescendantJunction_IsTraversedOnlyWithExplicitFollowLinks()
    {
        var exe = FindBinaryOrSkip();
        if (exe is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"turbo_descendant_{Guid.NewGuid():N}");
        var target = Path.Combine(Path.GetTempPath(), $"turbo_outside_{Guid.NewGuid():N}");
        var junction = Path.Combine(root, "outside-link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        var outsideFile = Path.Combine(target, "outside.txt");
        File.WriteAllText(outsideFile, "outside");
        CreateJunction(junction, target).Should().BeTrue("junction support is required for this Windows contract test");

        try
        {
            var safePaths = RunScanner(exe, "--path", root, "--threads", "1")
                .Select(record => record.GetProperty("path").GetString())
                .ToArray();
            safePaths.Should().NotContain(path =>
                string.Equals(path, Path.Combine(junction, "outside.txt"), StringComparison.OrdinalIgnoreCase));

            var followedPaths = RunScanner(
                    exe,
                    "--path", root,
                    "--threads", "1",
                    "--follow-links")
                .Select(record => record.GetProperty("path").GetString())
                .ToArray();
            followedPaths.Should().Contain(path =>
                string.Equals(path, Path.Combine(junction, "outside.txt"), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void SkipHidden_PrunesHiddenFilesAndHiddenDirectorySubtrees()
    {
        var exe = FindBinaryOrSkip();
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

    [Fact]
    public void MissingRoot_ReturnsNonZeroAndFatalError()
    {
        var exe = FindBinaryOrSkip();
        if (exe is null)
            return;

        var missingRoot = Path.Combine(Path.GetTempPath(), $"turbo_missing_{Guid.NewGuid():N}");
        var result = RunScannerProcess(exe, "--path", missingRoot, "--threads", "1");

        result.ExitCode.Should().NotBe(0);
        result.StandardOutput.Should().BeEmpty();
        result.StandardError.Should().Contain("ERROR:");
        result.StandardError.Should().Contain("cannot access scan root");
    }

    /// <summary>
    /// Reads the product version from Directory.Build.props rather than repeating it
    /// here. A literal in this test drifts on every release bump and fails the build
    /// for a reason that has nothing to do with the scanner contract.
    /// </summary>
    private static string ExpectedProductVersion()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var props = Path.Combine(directory.FullName, "Directory.Build.props");
            if (File.Exists(props))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    File.ReadAllText(props),
                    @"<StorageMasterVersion>([^<]+)</StorageMasterVersion>");
                if (match.Success)
                    return match.Groups[1].Value.Trim();
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate Directory.Build.props to read the expected product version.");
    }

    [Fact]
    public void VersionFlag_ReportsReleaseGateVersion()
    {
        var exe = FindBinaryOrSkip();
        if (exe is null)
            return;

        var result = RunScannerProcess(exe, "--version");

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Trim().Should().Be($"turbo-scanner {ExpectedProductVersion()}",
            "the native scanner ships alongside the app and its version gates the release");
        result.StandardError.Should().BeEmpty();
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static bool CreateJunction(string junction, string target)
    {
        var startInfo = new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{junction}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(junction);
    }

    private sealed record ScannerResult(int ExitCode, string StandardOutput, string StandardError);
}
