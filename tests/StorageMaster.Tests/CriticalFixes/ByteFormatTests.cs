using FluentAssertions;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.CriticalFixes;

/// <summary>
/// Regression tests for byte formatting. The old per-rule helpers used integer
/// division, so 1.9 GB rendered as "1.0 GB" in cleanup suggestions. Expected
/// values are built with the current culture — the decimal separator is
/// intentionally locale-aware, matching the UI's ByteSizeConverter.
/// </summary>
public sealed class ByteFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    public void Format_SmallSizes_UseByteSuffix(long bytes, string expected)
        => ByteFormat.Format(bytes).Should().Be(expected);

    [Theory]
    [InlineData(1024, 1.0, "KB")]
    [InlineData(1_572_864, 1.5, "MB")]
    [InlineData(2_040_109_465, 1.9, "GB")]   // integer division displayed this as 1.0 GB
    [InlineData(10L * 1024 * 1024 * 1024, 10.0, "GB")]
    public void Format_UsesFloatingPointDivision(long bytes, double expectedValue, string expectedUnit)
        => ByteFormat.Format(bytes).Should().Be($"{expectedValue:F1} {expectedUnit}");
}
