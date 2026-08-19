using FluentAssertions;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scheduling;

namespace StorageMaster.Tests.Scheduling;

public sealed class ScheduledCleanupPolicyTests
{
    // GetEffectiveRules is the only way to reach the safe default set — the
    // separate DefaultRuleTokens accessor was removed as unreferenced, so both
    // "no value" forms are pinned here to keep the unattended-cleanup default
    // set covered.
    [Theory]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData(null)]
    public void BlankRules_ExpandToExactCentralDefaults(string? rulesCsv)
    {
        ScheduledCleanupPolicy.GetEffectiveRules(rulesCsv).Should().Equal(
            "TempFiles",
            "CacheFolders",
            "BrowserCache",
            "WindowsUpdateCache",
            "DeliveryOptimization",
            "WindowsErrorReporting",
            "DownloadedInstallers");
    }

    // A rules string that parses to nothing must fall back to the same safe
    // defaults rather than to an empty rule set.
    [Fact]
    public void SeparatorOnlyRules_FallBackToCentralDefaults()
    {
        ScheduledCleanupPolicy.GetEffectiveRules(" , ; , ").Should().Equal(
            "TempFiles",
            "CacheFolders",
            "BrowserCache",
            "WindowsUpdateCache",
            "DeliveryOptimization",
            "WindowsErrorReporting",
            "DownloadedInstallers");
    }

    [Fact]
    public void ExplicitRules_AreTrimmedDeduplicatedAndDeterministic()
    {
        ScheduledCleanupPolicy.GetEffectiveRules(
                " TempFiles;browsercache, TEMPFILES ")
            .Should().Equal("browsercache", "TempFiles");
    }

    [Fact]
    public void GrantCurrentConsent_NormalizesPlanAndCreatesCurrentFingerprint()
    {
        var granted = ScheduledCleanupPolicy.GrantCurrentConsent(new ScheduledJobDefinition
        {
            Kind = ScheduledJobKind.CleanupExecuteSafe,
            TargetPath = @"C:\scan\.",
            StartTimeLocal = "9:05",
            RulesCsv = " TempFiles; BrowserCache ",
        });

        granted.TargetPath.Should().Be(@"C:\scan");
        granted.StartTimeLocal.Should().Be("09:05");
        granted.RulesCsv.Should().Be("BrowserCache,TempFiles");
        granted.DestructiveConsentVersion.Should().Be(
            ScheduledCleanupPolicy.CurrentConsentVersion);
        granted.DestructiveConsentFingerprint.Should().MatchRegex("^[0-9A-F]{64}$");
        ScheduledJobExecutionPolicy.Evaluate(true, granted with { Enabled = true })
            .CanExecute.Should().BeTrue();
    }

    [Fact]
    public void Fingerprint_IgnoresStoredConsentFieldsAndEquivalentRuleOrder()
    {
        var job = DestructiveJob() with
        {
            RulesCsv = "TempFiles,BrowserCache",
            DestructiveConsentVersion = 41,
            DestructiveConsentFingerprint = "stale",
        };
        var equivalent = job with
        {
            RulesCsv = "browsercache; tempfiles",
            DestructiveConsentVersion = 0,
            DestructiveConsentFingerprint = string.Empty,
        };

        ScheduledCleanupPolicy.CreateConsentFingerprint(job).Should().Be(
            ScheduledCleanupPolicy.CreateConsentFingerprint(equivalent));
    }

    [Theory]
    [InlineData(CleanupRisk.Safe, true, true)]
    [InlineData(CleanupRisk.Low, true, true)]
    [InlineData(CleanupRisk.Medium, true, false)]
    [InlineData(CleanupRisk.High, true, false)]
    [InlineData(CleanupRisk.Low, false, false)]
    public void Eligibility_RequiresSafeOrLowRiskAndRecycleBinSupport(
        CleanupRisk risk,
        bool supportsRecycleBin,
        bool expected)
    {
        var suggestion = new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = "test",
            Title = "test",
            Description = "test",
            Category = CleanupCategory.TempFiles,
            Risk = risk,
            EstimatedBytes = 1,
            SupportsRecycleBin = supportsRecycleBin,
            TargetPaths = [@"C:\test.tmp"],
        };

        ScheduledCleanupPolicy.IsEligibleSuggestion(suggestion).Should().Be(expected);
    }

    [Fact]
    public void ExecutionOverrides_DisableEntireDownloadsExpansion()
    {
        var settings = new AppSettings { ClearEntireDownloads = true };

        ScheduledCleanupPolicy.ApplyExecutionSafetyOverrides(settings);

        settings.ClearEntireDownloads.Should().BeFalse();
    }

    [Fact]
    public void Selection_RejectsMatchingHighRiskAndIgnoresUnconsentedRules()
    {
        var safe = Suggestion("core.temp", CleanupCategory.TempFiles, CleanupRisk.Low);
        var high = Suggestion("core.leftovers", CleanupCategory.ProgramLeftovers, CleanupRisk.High);
        var unrelated = Suggestion("core.browser", CleanupCategory.BrowserCache, CleanupRisk.Low);

        var selection = ScheduledCleanupPolicy.SelectEligibleSuggestions(
            [safe, high, unrelated],
            "TempFiles,ProgramLeftovers");

        selection.MatchedSuggestionCount.Should().Be(2);
        selection.RejectedSuggestionCount.Should().Be(1);
        selection.EligibleSuggestions.Should().ContainSingle().Which.Should().BeSameAs(safe);
    }

    [Fact]
    public void ConsentFingerprint_BindsEveryDestructivePlanField()
    {
        var granted = ScheduledCleanupPolicy.GrantCurrentConsent(DestructiveJob() with
        {
            Frequency = ScheduledJobFrequency.Weekly,
            WeeklyDay = DayOfWeek.Monday,
        });

        var changedPlans = new[]
        {
            granted with { TargetPath = @"C:\different" },
            granted with { RulesCsv = "TempFiles" },
            granted with { StartTimeLocal = "10:30" },
            granted with { Frequency = ScheduledJobFrequency.Daily },
            granted with { WeeklyDay = DayOfWeek.Tuesday },
        };

        changedPlans.Should().OnlyContain(job =>
            ScheduledJobExecutionPolicy.Evaluate(true, job).BlockReason ==
            ScheduledJobExecutionBlockReason.DestructiveConsentPlanChanged);
    }

    [Fact]
    public void InvalidTime_CannotReceiveConsent()
    {
        var action = () => ScheduledCleanupPolicy.GrantCurrentConsent(
            DestructiveJob() with { StartTimeLocal = "25:99" });

        action.Should().Throw<InvalidDataException>()
            .WithMessage("*HH:mm*");
    }

    private static ScheduledJobDefinition DestructiveJob() => new()
    {
        Kind = ScheduledJobKind.CleanupExecuteSafe,
        Enabled = true,
        TargetPath = @"C:\scan",
        StartTimeLocal = "09:15",
    };

    private static CleanupSuggestion Suggestion(
        string ruleId,
        CleanupCategory category,
        CleanupRisk risk) => new()
        {
            Id = Guid.NewGuid(),
            RuleId = ruleId,
            Title = ruleId,
            Description = ruleId,
            Category = category,
            Risk = risk,
            EstimatedBytes = 1,
            TargetPaths = [@"C:\test.tmp"],
        };
}
