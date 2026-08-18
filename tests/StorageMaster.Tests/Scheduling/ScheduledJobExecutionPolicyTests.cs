using FluentAssertions;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scheduling;

namespace StorageMaster.Tests.Scheduling;

public sealed class ScheduledJobExecutionPolicyTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Evaluate_GlobalSchedulingDisabled_DeniesExecution(
        bool scheduledTasksEnabled,
        bool jobEnabled)
    {
        var decision = ScheduledJobExecutionPolicy.Evaluate(
            scheduledTasksEnabled,
            jobEnabled);

        decision.CanExecute.Should().BeFalse();
        decision.BlockReason.Should().Be(
            ScheduledJobExecutionBlockReason.GlobalSchedulingDisabled);
    }

    [Fact]
    public void Evaluate_EnabledGloballyButJobDisabled_DeniesExecution()
    {
        var decision = ScheduledJobExecutionPolicy.Evaluate(
            scheduledTasksEnabled: true,
            jobEnabled: false);

        decision.CanExecute.Should().BeFalse();
        decision.BlockReason.Should().Be(
            ScheduledJobExecutionBlockReason.JobDisabled);
    }

    [Fact]
    public void Evaluate_GlobalAndJobEnabled_AllowsExecution()
    {
        var decision = ScheduledJobExecutionPolicy.Evaluate(
            scheduledTasksEnabled: true,
            jobEnabled: true);

        decision.CanExecute.Should().BeTrue();
        decision.BlockReason.Should().Be(ScheduledJobExecutionBlockReason.None);
    }

    [Fact]
    public void Evaluate_EnabledDestructiveJobWithoutCurrentConsent_DeniesExecution()
    {
        var job = new ScheduledJobDefinition
        {
            Kind = ScheduledJobKind.CleanupExecuteSafe,
            Enabled = true,
            DestructiveConsentVersion = 0,
        };

        var decision = ScheduledJobExecutionPolicy.Evaluate(true, job);

        decision.CanExecute.Should().BeFalse();
        decision.BlockReason.Should().Be(
            ScheduledJobExecutionBlockReason.MissingDestructiveConsent);
    }

    [Fact]
    public void Evaluate_EnabledDestructiveJobWithCurrentConsent_AllowsExecution()
    {
        var job = ScheduledCleanupPolicy.GrantCurrentConsent(new ScheduledJobDefinition
        {
            Kind = ScheduledJobKind.CleanupExecuteSafe,
            Enabled = true,
            TargetPath = @"C:\",
            StartTimeLocal = "09:00",
        });

        ScheduledJobExecutionPolicy.Evaluate(true, job).CanExecute.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_DestructivePlanChangedAfterConsent_DeniesExecution()
    {
        var consented = ScheduledCleanupPolicy.GrantCurrentConsent(new ScheduledJobDefinition
        {
            Kind = ScheduledJobKind.CleanupExecuteSafe,
            Enabled = true,
            TargetPath = @"C:\before",
            StartTimeLocal = "09:00",
        });

        var decision = ScheduledJobExecutionPolicy.Evaluate(
            true,
            consented with { TargetPath = @"C:\after" });

        decision.CanExecute.Should().BeFalse();
        decision.BlockReason.Should().Be(
            ScheduledJobExecutionBlockReason.DestructiveConsentPlanChanged);
    }
}
