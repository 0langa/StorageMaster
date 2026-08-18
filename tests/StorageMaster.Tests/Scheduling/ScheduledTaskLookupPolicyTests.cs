using FluentAssertions;
using StorageMaster.Core.Scheduling;

namespace StorageMaster.Tests.Scheduling;

public sealed class ScheduledTaskLookupPolicyTests
{
    [Fact]
    public void Evaluate_TargetedQuerySucceeded_ReturnsFound()
    {
        ScheduledTaskLookupPolicy.Evaluate(0, 1, string.Empty, "StorageMaster Job A")
            .Should().Be(ScheduledTaskLookupStatus.Found);
    }

    [Fact]
    public void Evaluate_TargetedAndInventoryQueriesFail_ReturnsQueryFailed()
    {
        ScheduledTaskLookupPolicy.Evaluate(1, 1, string.Empty, "StorageMaster Job A")
            .Should().Be(ScheduledTaskLookupStatus.QueryFailed);
    }

    [Fact]
    public void Evaluate_SuccessfulInventoryWithoutTask_ReturnsNotFound()
    {
        const string inventory = "\"\\Other Task\",\"N/A\",\"Ready\"";

        ScheduledTaskLookupPolicy.Evaluate(1, 0, inventory, "StorageMaster Job A")
            .Should().Be(ScheduledTaskLookupStatus.NotFound);
    }

    [Fact]
    public void Evaluate_InventoryStillContainsTask_ReturnsQueryFailed()
    {
        const string inventory =
            "\"\\StorageMaster Job A\",\"N/A\",\"Ready\"\r\n" +
            "\"\\Other Task\",\"N/A\",\"Ready\"";

        ScheduledTaskLookupPolicy.Evaluate(1, 0, inventory, "StorageMaster Job A")
            .Should().Be(ScheduledTaskLookupStatus.QueryFailed);
    }

    [Fact]
    public void Evaluate_QuotedTaskName_ParsesEscapedQuotes()
    {
        const string inventory = "\"\\StorageMaster \"\"Legacy\"\"\",\"N/A\",\"Ready\"";

        ScheduledTaskLookupPolicy.Evaluate(1, 0, inventory, "StorageMaster \"Legacy\"")
            .Should().Be(ScheduledTaskLookupStatus.QueryFailed);
    }
}
