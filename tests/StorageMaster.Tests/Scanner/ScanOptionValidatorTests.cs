using FluentAssertions;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;

namespace StorageMaster.Tests.Scanner;

public sealed class ScanOptionValidatorTests
{
    [Fact]
    public void NormalizeAndValidate_NormalizesDriveRootWithoutDisplayNameLoss()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var options = ScanOptionValidator.NormalizeAndValidate(new ScanOptions
        {
            RootPath = root,
            MaxParallelism = 1,
        });

        options.RootPath.Should().Be(Path.GetFullPath(root));
        ScanOptionValidator.GetDisplayName(options.RootPath).Should().Be(options.RootPath);
    }

    [Fact]
    public void NormalizeAndValidate_ClampsInvalidParallelismAndBatchSize()
    {
        var options = ScanOptionValidator.NormalizeAndValidate(new ScanOptions
        {
            RootPath = Path.GetTempPath(),
            MaxParallelism = -10,
            DbBatchSize = int.MaxValue,
        });

        options.MaxParallelism.Should().BeGreaterThan(0);
        options.DbBatchSize.Should().Be(ScanOptionValidator.MaximumDbBatchSize);
    }

    [Fact]
    public void IsPathEqualOrUnder_RequiresDirectoryBoundary()
    {
        ScanOptionValidator.IsPathEqualOrUnder(@"C:\Windows\Installer", @"C:\Windows\Installer")
            .Should().BeTrue();
        ScanOptionValidator.IsPathEqualOrUnder(@"C:\Windows\Installer\cache", @"C:\Windows\Installer")
            .Should().BeTrue();
        ScanOptionValidator.IsPathEqualOrUnder(@"C:\Windows\InstallerBackup", @"C:\Windows\Installer")
            .Should().BeFalse();
    }
}
