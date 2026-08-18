namespace StorageMaster.Core.Models;

public enum ScheduledJobKind
{
    Scan,
    ScanAndReport,
    CleanupAnalyze,
    CleanupExecuteSafe,
}

public enum ScheduledJobFrequency
{
    Daily,
    Weekly,
}

public sealed record ScheduledJobDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public ScheduledJobKind Kind { get; init; } = ScheduledJobKind.Scan;
    public ScheduledJobFrequency Frequency { get; init; } = ScheduledJobFrequency.Daily;
    public string StartTimeLocal { get; init; } = "09:00";
    public DayOfWeek WeeklyDay { get; init; } = DayOfWeek.Monday;
    public string TargetPath { get; init; } = string.Empty;
    public string RulesCsv { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// Consent contract accepted when enabling unattended destructive cleanup.
    /// Zero means no current explicit consent and fails closed at execution.
    /// </summary>
    public int DestructiveConsentVersion { get; init; }
    /// <summary>
    /// SHA-256 fingerprint of the normalized unattended-cleanup plan accepted
    /// by the user. Empty or stale fingerprints fail closed at execution.
    /// </summary>
    public string DestructiveConsentFingerprint { get; init; } = string.Empty;
    public DateTime? LastRunUtc { get; init; }
    public string LastStatus { get; init; } = string.Empty;
    public string LastMessage { get; init; } = string.Empty;
}

public sealed record ScheduledTaskInfo
{
    public required ScheduledJobDefinition Job { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public string NextRunTimeText { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
}
