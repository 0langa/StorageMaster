using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scheduling;

public enum ScheduledJobExecutionBlockReason
{
    None,
    GlobalSchedulingDisabled,
    JobDisabled,
    MissingDestructiveConsent,
    DestructiveConsentPlanChanged,
}

public readonly record struct ScheduledJobExecutionDecision(
    ScheduledJobExecutionBlockReason BlockReason)
{
    public bool CanExecute => BlockReason == ScheduledJobExecutionBlockReason.None;
}

public static class ScheduledJobExecutionPolicy
{
    public const int CurrentDestructiveConsentVersion = ScheduledCleanupPolicy.CurrentConsentVersion;

    public static ScheduledJobExecutionDecision Evaluate(
        bool scheduledTasksEnabled,
        bool jobEnabled)
    {
        if (!scheduledTasksEnabled)
            return new ScheduledJobExecutionDecision(
                ScheduledJobExecutionBlockReason.GlobalSchedulingDisabled);

        return jobEnabled
            ? new ScheduledJobExecutionDecision(ScheduledJobExecutionBlockReason.None)
            : new ScheduledJobExecutionDecision(ScheduledJobExecutionBlockReason.JobDisabled);
    }

    public static ScheduledJobExecutionDecision Evaluate(
        bool scheduledTasksEnabled,
        ScheduledJobDefinition job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var enabledDecision = Evaluate(scheduledTasksEnabled, job.Enabled);
        if (!enabledDecision.CanExecute)
            return enabledDecision;

        if (job.Kind == ScheduledJobKind.CleanupExecuteSafe &&
            (job.DestructiveConsentVersion != CurrentDestructiveConsentVersion ||
             string.IsNullOrWhiteSpace(job.DestructiveConsentFingerprint)))
        {
            return new ScheduledJobExecutionDecision(
                ScheduledJobExecutionBlockReason.MissingDestructiveConsent);
        }

        if (job.Kind == ScheduledJobKind.CleanupExecuteSafe)
        {
            string expectedFingerprint;
            try
            {
                expectedFingerprint = ScheduledCleanupPolicy.CreateConsentFingerprint(job);
            }
            catch (Exception)
            {
                return new ScheduledJobExecutionDecision(
                    ScheduledJobExecutionBlockReason.DestructiveConsentPlanChanged);
            }

            if (!string.Equals(
                    job.DestructiveConsentFingerprint,
                    expectedFingerprint,
                    StringComparison.Ordinal))
            {
                return new ScheduledJobExecutionDecision(
                    ScheduledJobExecutionBlockReason.DestructiveConsentPlanChanged);
            }
        }

        return enabledDecision;
    }
}
