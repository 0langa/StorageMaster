using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scheduling;

public sealed record ScheduledCleanupSelection(
    IReadOnlyList<CleanupSuggestion> EligibleSuggestions,
    int MatchedSuggestionCount)
{
    public int RejectedSuggestionCount => MatchedSuggestionCount - EligibleSuggestions.Count;
}

/// <summary>
/// Defines the immutable safety contract for unattended cleanup jobs.
/// UI confirmation and headless execution must both use this policy so blank
/// rules, normalized schedule fields, and eligible cleanup risks cannot drift.
/// </summary>
public static class ScheduledCleanupPolicy
{
    public const int CurrentConsentVersion = 2;

    private const string FingerprintPolicyMarker =
        "StorageMaster.ScheduledCleanup/v2|method=RecycleBin|risk=Safe,Low|clear-entire-downloads=false";

    private static readonly IReadOnlyList<string> Defaults = Array.AsReadOnly(
    [
        "TempFiles",
        "CacheFolders",
        "BrowserCache",
        "WindowsUpdateCache",
        "DeliveryOptimization",
        "WindowsErrorReporting",
        "DownloadedInstallers",
    ]);

    /// <summary>Exact rules used when a scheduled cleanup job leaves RulesCsv blank.</summary>
    public static IReadOnlyList<string> DefaultRuleTokens => Defaults;

    /// <summary>
    /// Expands blank rules to the exact safe default set and canonicalizes
    /// explicit rules for consent, storage, and execution.
    /// </summary>
    public static IReadOnlyList<string> GetEffectiveRules(string? rulesCsv)
    {
        if (string.IsNullOrWhiteSpace(rulesCsv))
            return Defaults;

        var rules = rulesCsv
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static rule => !string.IsNullOrWhiteSpace(rule))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static rule => rule, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return rules.Length == 0 ? Defaults : Array.AsReadOnly(rules);
    }

    /// <summary>
    /// Normalizes every field that changes when or where unattended cleanup
    /// runs. Invalid destructive plans are rejected before consent is stored.
    /// </summary>
    public static ScheduledJobDefinition NormalizeConsentFields(ScheduledJobDefinition job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Kind != ScheduledJobKind.CleanupExecuteSafe)
            throw new InvalidDataException("Destructive cleanup consent applies only to CleanupExecuteSafe jobs.");
        if (!Enum.IsDefined(job.Frequency))
            throw new InvalidDataException("Scheduled cleanup frequency is invalid.");
        if (job.Frequency == ScheduledJobFrequency.Weekly && !Enum.IsDefined(job.WeeklyDay))
            throw new InvalidDataException("Scheduled cleanup weekly day is invalid.");

        var targetPath = job.TargetPath.Trim();
        if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath))
            throw new InvalidDataException("Scheduled cleanup target must be a fully qualified path.");

        targetPath = Path.GetFullPath(targetPath);
        var root = Path.GetPathRoot(targetPath);
        if (!string.Equals(targetPath, root, StringComparison.OrdinalIgnoreCase))
            targetPath = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!TimeOnly.TryParseExact(
                job.StartTimeLocal.Trim(),
                ["H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startTime))
        {
            throw new InvalidDataException("Scheduled cleanup start time must use HH:mm (24-hour time).");
        }

        return job with
        {
            TargetPath = targetPath,
            StartTimeLocal = startTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            RulesCsv = string.Join(',', GetEffectiveRules(job.RulesCsv)),
        };
    }

    /// <summary>
    /// Creates a deterministic fingerprint for the normalized destructive
    /// plan. Existing consent fields are deliberately ignored.
    /// </summary>
    public static string CreateConsentFingerprint(ScheduledJobDefinition job)
    {
        var normalized = NormalizeConsentFields(job);
        var rules = GetEffectiveRules(normalized.RulesCsv)
            .Select(static rule => rule.ToUpperInvariant())
            .OrderBy(static rule => rule, StringComparer.Ordinal)
            .ToArray();
        var weeklyDay = normalized.Frequency == ScheduledJobFrequency.Weekly
            ? normalized.WeeklyDay.ToString()
            : "not-applicable";

        var fingerprintFields = new[]
        {
            FingerprintPolicyMarker,
            $"consent-version={CurrentConsentVersion}",
            $"kind={normalized.Kind}",
            $"target={normalized.TargetPath.ToUpperInvariant()}",
            $"rules={string.Join(',', rules)}",
            $"frequency={normalized.Frequency}",
            $"weekly-day={weeklyDay}",
            $"start-time={normalized.StartTimeLocal}",
        };
        var canonical = string.Concat(fingerprintFields.Select(static field =>
            $"{field.Length.ToString(CultureInfo.InvariantCulture)}:{field}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Normalizes a destructive job and records current plan consent.</summary>
    public static ScheduledJobDefinition GrantCurrentConsent(ScheduledJobDefinition job)
    {
        var normalized = NormalizeConsentFields(job);
        return normalized with
        {
            DestructiveConsentVersion = CurrentConsentVersion,
            DestructiveConsentFingerprint = CreateConsentFingerprint(normalized),
        };
    }

    /// <summary>Applies fixed execution overrides covered by the consent fingerprint.</summary>
    public static void ApplyExecutionSafetyOverrides(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ClearEntireDownloads = false;
    }

    /// <summary>
    /// Resolves consented rules and returns only matching suggestions that are
    /// eligible for unattended Recycle Bin cleanup.
    /// </summary>
    public static ScheduledCleanupSelection SelectEligibleSuggestions(
        IEnumerable<CleanupSuggestion> suggestions,
        string? rulesCsv)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        var rules = GetEffectiveRules(rulesCsv);
        var matching = suggestions
            .Where(suggestion => rules.Any(rule =>
                string.Equals(suggestion.RuleId, rule, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(suggestion.Category.ToString(), rule, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var eligible = matching.Where(IsEligibleSuggestion).ToList();
        return new ScheduledCleanupSelection(eligible, matching.Count);
    }

    /// <summary>Only recoverable safe/low-risk suggestions may run unattended.</summary>
    public static bool IsEligibleSuggestion(CleanupSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return suggestion.SupportsRecycleBin &&
               suggestion.Risk is CleanupRisk.Safe or CleanupRisk.Low;
    }
}
