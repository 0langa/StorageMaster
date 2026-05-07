using FluentAssertions;
using StorageMaster.Core.Safety;

namespace StorageMaster.Tests.CriticalFixes;

public sealed class SafeTempDirectoryTests
{
    [Fact]
    public void TryDelete_RefusesPathOutsideDirectTempChild()
    {
        var outside = Path.GetPathRoot(Path.GetTempPath()) ?? Environment.GetFolderPath(Environment.SpecialFolder.System);

        var deleted = SafeTempDirectory.TryDelete(outside, "sm_diag");

        deleted.Should().BeFalse();
        Directory.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public void TryDelete_DeletesOnlyGuardedTempChild()
    {
        var dir = SafeTempDirectory.Create("sm_guard_test");
        File.WriteAllText(Path.Combine(dir, "probe.txt"), "data");

        SafeTempDirectory.TryDelete(dir, "sm_guard_test").Should().BeTrue();

        Directory.Exists(dir).Should().BeFalse();
    }
}
