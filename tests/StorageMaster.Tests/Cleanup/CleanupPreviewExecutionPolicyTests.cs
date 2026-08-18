using FluentAssertions;
using StorageMaster.Core.Cleanup;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class CleanupPreviewExecutionPolicyTests
{
    [Fact]
    public void CompleteSuccessfulDryRun_AllowsFollowUpExecution()
    {
        var results = new[] { Result(CleanupResultStatus.Success) };

        CleanupPreviewExecutionPolicy.CanExecuteAfterPreview(
            results,
            [results[0].SuggestionId]).Should().BeTrue();
    }

    [Theory]
    [InlineData(CleanupResultStatus.PartialSuccess)]
    [InlineData(CleanupResultStatus.Failed)]
    [InlineData(CleanupResultStatus.Skipped)]
    public void NonSuccessResult_BlocksFollowUpExecution(CleanupResultStatus status)
    {
        var results = new[] { Result(status) };

        CleanupPreviewExecutionPolicy.CanExecuteAfterPreview(
            results,
            [results[0].SuggestionId]).Should().BeFalse();
    }

    [Fact]
    public void MissingResult_BlocksFollowUpExecution()
    {
        var results = new[] { Result(CleanupResultStatus.Success) };

        CleanupPreviewExecutionPolicy.CanExecuteAfterPreview(
            results,
            [results[0].SuggestionId, Guid.NewGuid()]).Should().BeFalse();
    }

    [Fact]
    public void RealRunResult_BlocksFollowUpExecution()
    {
        var results = new[] { Result(CleanupResultStatus.Success) with { WasDryRun = false } };

        CleanupPreviewExecutionPolicy.CanExecuteAfterPreview(
            results,
            [results[0].SuggestionId]).Should().BeFalse();
    }

    [Fact]
    public void ErrorOrFailedPath_BlocksFollowUpExecution()
    {
        var result = Result(CleanupResultStatus.Success) with
        {
            ErrorMessage = "audit failed",
            FailedPaths = [@"C:\\temp\\failed.tmp"],
        };

        CleanupPreviewExecutionPolicy.CanExecuteAfterPreview(
            [result],
            [result.SuggestionId]).Should().BeFalse();
    }

    [Fact]
    public void DuplicateResultId_WithMissingExpectedId_BlocksFollowUpExecution()
    {
        var first = Result(CleanupResultStatus.Success);
        var duplicate = first with { ExecutedUtc = first.ExecutedUtc.AddTicks(1) };

        CleanupPreviewExecutionPolicy.CanExecuteAfterPreview(
            [first, duplicate],
            [first.SuggestionId, Guid.NewGuid()]).Should().BeFalse();
    }

    private static CleanupResult Result(CleanupResultStatus status) => new()
    {
        SuggestionId = Guid.NewGuid(),
        Status = status,
        BytesFreed = 1,
        ExecutedUtc = DateTime.UtcNow,
        WasDryRun = true,
    };
}
