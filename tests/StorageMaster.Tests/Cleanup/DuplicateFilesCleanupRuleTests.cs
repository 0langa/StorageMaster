using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class DuplicateFilesCleanupRuleTests
{
    [Fact]
    public async Task AnalyzeAsync_NeverExposesRawDuplicatePathsToGenericCleanup()
    {
        var repo = new Mock<IDuplicateRepository>(MockBehavior.Strict);
        var rule = new DuplicateFilesCleanupRule(repo.Object);
        var suggestions = new List<CleanupSuggestion>();

        await foreach (var suggestion in rule.AnalyzeAsync(7, new AppSettings()))
            suggestions.Add(suggestion);

        suggestions.Should().BeEmpty(
            "duplicate removal must use the journaled, keeper-validating Duplicates workflow");
        repo.VerifyNoOtherCalls();
    }
}
