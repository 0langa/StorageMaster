using FluentAssertions;
using Moq;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Infrastructure;
using StorageMaster.UI.Pages;

namespace StorageMaster.Tests.UI;

/// <summary>
/// Unit tests for SettingsViewModel computed properties, category selection,
/// search filtering, and update state. No WinUI shell required.
/// </summary>
public sealed class SettingsViewModelTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static SettingsViewModel BuildVm(
        Mock<IUpdateService>? update = null,
        Mock<IScanRepository>? scanRepo = null)
    {
        var repo = new Mock<ISettingsRepository>();
        repo.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings());

        var upd = update ?? new Mock<IUpdateService>();
        var sr = scanRepo ?? new Mock<IScanRepository>();
        var diag = new Mock<ILocalDiagnosticsService>();
        var sched = new Mock<IScheduledTaskService>();

        return new SettingsViewModel(
            repo.Object,
            upd.Object,
            sr.Object,
            diag.Object,
            sched.Object,
            new StartupRegistrationService());
    }

    // ── SelectedCategoryTitle ─────────────────────────────────────────────────

    [Theory]
    [InlineData(SettingsCategory.General, "General & Appearance")]
    [InlineData(SettingsCategory.Scanning, "Scanning & Performance")]
    [InlineData(SettingsCategory.Cleanup, "Cleanup & Safety")]
    [InlineData(SettingsCategory.Duplicates, "Duplicates & Matching")]
    [InlineData(SettingsCategory.ResultsHistory, "Results & History")]
    [InlineData(SettingsCategory.Scheduling, "Scheduling & Automation")]
    [InlineData(SettingsCategory.TrayNotifications, "Background, Tray & Notifications")]
    [InlineData(SettingsCategory.Updates, "Updates & Security")]
    [InlineData(SettingsCategory.AdvancedDiagnostics, "Advanced Diagnostics & About")]
    public void SelectedCategoryTitle_ReturnsCorrectString(SettingsCategory category, string expected)
    {
        var vm = BuildVm();
        vm.SelectedCategory = category;

        vm.SelectedCategoryTitle.Should().Be(expected);
    }

    // ── IsXSelected properties ────────────────────────────────────────────────

    [Theory]
    [InlineData(SettingsCategory.General)]
    [InlineData(SettingsCategory.Scanning)]
    [InlineData(SettingsCategory.Cleanup)]
    [InlineData(SettingsCategory.Duplicates)]
    [InlineData(SettingsCategory.ResultsHistory)]
    [InlineData(SettingsCategory.Scheduling)]
    [InlineData(SettingsCategory.TrayNotifications)]
    [InlineData(SettingsCategory.Updates)]
    [InlineData(SettingsCategory.AdvancedDiagnostics)]
    public void IsXSelected_TrueOnlyForSelectedCategory(SettingsCategory selected)
    {
        var vm = BuildVm();
        vm.SelectedCategory = selected;

        vm.IsGeneralSelected.Should().Be(selected == SettingsCategory.General);
        vm.IsScanningSelected.Should().Be(selected == SettingsCategory.Scanning);
        vm.IsCleanupSelected.Should().Be(selected == SettingsCategory.Cleanup);
        vm.IsDuplicatesSelected.Should().Be(selected == SettingsCategory.Duplicates);
        vm.IsResultsHistorySelected.Should().Be(selected == SettingsCategory.ResultsHistory);
        vm.IsSchedulingSelected.Should().Be(selected == SettingsCategory.Scheduling);
        vm.IsTrayNotificationsSelected.Should().Be(selected == SettingsCategory.TrayNotifications);
        vm.IsUpdatesSelected.Should().Be(selected == SettingsCategory.Updates);
        vm.IsAdvancedDiagnosticsSelected.Should().Be(selected == SettingsCategory.AdvancedDiagnostics);
    }

    // ── FilteredCategories / SearchQuery ──────────────────────────────────────

    [Fact]
    public void FilteredCategories_InitiallyContainsAllNineCategories()
    {
        var vm = BuildVm();

        vm.FilteredCategories.Should().HaveCount(9);
    }

    [Fact]
    public void SearchQuery_MatchingTerm_FiltersToMatchingCategories()
    {
        var vm = BuildVm();
        vm.SearchQuery = "Scan";

        // "Scanning & Performance" should match; others that don't contain "Scan"
        // in their title or description should be excluded.
        vm.FilteredCategories.Should().NotBeEmpty();
        vm.FilteredCategories.Should().AllSatisfy(c =>
            (c.Title + c.Description).Contains("Scan", StringComparison.OrdinalIgnoreCase)
                .Should().BeTrue($"'{c.Title}' should match search 'Scan'"));
    }

    [Fact]
    public void SearchQuery_EmptyAfterFilter_RestoresAllCategories()
    {
        var vm = BuildVm();
        vm.SearchQuery = "Scan";
        vm.FilteredCategories.Should().NotHaveCount(9);

        vm.SearchQuery = string.Empty;

        vm.FilteredCategories.Should().HaveCount(9);
    }

    [Fact]
    public void SearchQuery_NoMatch_ResultsInEmptyList()
    {
        var vm = BuildVm();
        vm.SearchQuery = "xyzzy_no_match_42";

        vm.FilteredCategories.Should().BeEmpty();
    }

    // ── CanCheckForUpdates / CanDownloadAndInstall ────────────────────────────

    [Fact]
    public void CanCheckForUpdates_DefaultState_IsTrue()
    {
        var vm = BuildVm();

        vm.CanCheckForUpdates.Should().BeTrue();
    }

    [Fact]
    public void CanCheckForUpdates_WhenCheckingForUpdates_IsFalse()
    {
        var vm = BuildVm();
        vm.IsCheckingForUpdates = true;

        vm.CanCheckForUpdates.Should().BeFalse();
    }

    [Fact]
    public void CanCheckForUpdates_WhenDownloadingUpdate_IsFalse()
    {
        var vm = BuildVm();
        vm.IsDownloadingUpdate = true;

        vm.CanCheckForUpdates.Should().BeFalse();
    }

    [Fact]
    public void CanDownloadAndInstall_NoUpdateAvailable_IsFalse()
    {
        var vm = BuildVm();

        vm.CanDownloadAndInstall.Should().BeFalse();
    }

    [Fact]
    public void CanDownloadAndInstall_UpdateAvailableAndIdle_IsTrue()
    {
        var vm = BuildVm();
        vm.AvailableUpdate = MakeUpdateInfo("2.9.0");

        vm.CanDownloadAndInstall.Should().BeTrue();
    }

    [Fact]
    public void CanDownloadAndInstall_WhileDownloading_IsFalse()
    {
        var vm = BuildVm();
        vm.AvailableUpdate = MakeUpdateInfo("2.9.0");
        vm.IsDownloadingUpdate = true;

        vm.CanDownloadAndInstall.Should().BeFalse();
    }

    // ── UpdateAvailableText ───────────────────────────────────────────────────

    [Fact]
    public void UpdateAvailableText_NoUpdate_IsEmpty()
    {
        var vm = BuildVm();
        vm.AvailableUpdate = null;

        vm.UpdateAvailableText.Should().BeNullOrEmpty();
    }

    [Fact]
    public void UpdateAvailableText_WithUpdate_ContainsVersion()
    {
        var vm = BuildVm();
        vm.AvailableUpdate = MakeUpdateInfo("2.9.0");

        vm.UpdateAvailableText.Should().Contain("2.9.0");
    }

    // ── IsEditorOpen / SelectedCategory interaction ───────────────────────────

    [Fact]
    public void IsEditorOpen_DefaultState_IsFalse()
    {
        var vm = BuildVm();

        vm.IsEditorOpen.Should().BeFalse();
    }

    [Fact]
    public void SelectedCategory_Change_RaisesPropertyChangedForTitle()
    {
        var vm = BuildVm();
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.SelectedCategory = SettingsCategory.Updates;

        notified.Should().Contain(nameof(SettingsViewModel.SelectedCategoryTitle));
    }

    // ── HasUpdateAvailable ────────────────────────────────────────────────────

    [Fact]
    public void HasUpdateAvailable_NullUpdate_IsFalse()
    {
        var vm = BuildVm();
        vm.AvailableUpdate = null;

        vm.HasUpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public void HasUpdateAvailable_WithUpdate_IsTrue()
    {
        var vm = BuildVm();
        vm.AvailableUpdate = MakeUpdateInfo("3.0.0");

        vm.HasUpdateAvailable.Should().BeTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static UpdateInfo MakeUpdateInfo(string version) => new()
    {
        Version = Version.Parse(version),
        TagName = $"v{version}",
        ReleaseNotes = "Release notes",
        AssetName = $"StorageMaster-{version}-win-x64-Setup.exe",
        DownloadUrl = $"https://github.com/0langa/StorageMaster/releases/download/v{version}/StorageMaster-{version}-win-x64-Setup.exe",
        ReleaseUrl = $"https://github.com/0langa/StorageMaster/releases/tag/v{version}",
        PublishedAt = DateTimeOffset.UtcNow,
    };
}
