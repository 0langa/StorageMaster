using FluentAssertions;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class CleanupSuggestionSelectionPolicyTests
{
    [Theory]
    [InlineData(CleanupRisk.Safe, true)]
    [InlineData(CleanupRisk.Low, true)]
    [InlineData(CleanupRisk.Medium, false)]
    [InlineData(CleanupRisk.High, false)]
    public void ShouldSelectByDefault_RequiresRecoverableLowRisk(
        CleanupRisk risk,
        bool expected)
    {
        var suggestion = MakeSuggestion(risk, supportsRecycleBin: true);

        CleanupSuggestionSelectionPolicy.ShouldSelectByDefault(suggestion)
            .Should().Be(expected);
    }

    [Fact]
    public void ShouldSelectByDefault_PermanentOnlySuggestion_IsNeverSelected()
    {
        var suggestion = MakeSuggestion(CleanupRisk.Safe, supportsRecycleBin: false);

        CleanupSuggestionSelectionPolicy.ShouldSelectByDefault(suggestion)
            .Should().BeFalse();
    }

    private static CleanupSuggestion MakeSuggestion(
        CleanupRisk risk,
        bool supportsRecycleBin) => new()
        {
            Id = Guid.NewGuid(),
            RuleId = "test.selection-policy",
            Title = "Selection policy",
            Description = "Test suggestion",
            Category = CleanupCategory.TempFiles,
            Risk = risk,
            EstimatedBytes = 1,
            SupportsRecycleBin = supportsRecycleBin,
            TargetPaths = [@"C:\Temp\test.tmp"],
        };
}
