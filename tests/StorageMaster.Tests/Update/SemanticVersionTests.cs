using System.Globalization;
using FluentAssertions;
using StorageMaster.Core.Update;

namespace StorageMaster.Tests.Update;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("v2147483648.0.0")]
    [InlineData("v0.2147483648.0")]
    [InlineData("v0.0.2147483648")]
    public void TryParseTag_OverflowingCoreComponent_ReturnsFalse(string tag)
    {
        SemanticVersion.TryParseTag(tag, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseTag_CultureSpecificDigits_ReturnFalseWithoutThrowing()
    {
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            SemanticVersion.TryParseTag("v1٢.0.0", out _).Should().BeFalse();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
