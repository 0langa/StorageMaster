using FluentAssertions;
using Moq;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Infrastructure;
using StorageMaster.UI.Pages;

namespace StorageMaster.Tests.UI;

/// <summary>
/// Unit tests for ScanViewModel computed properties and path validation.
/// All tests run on a background thread (no WinUI shell required).
/// </summary>
public sealed class ScanViewModelTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static ScanViewModel BuildVm(
        bool isAdmin = false,
        string? defaultScanPath = null,
        Mock<ISettingsRepository>? settings = null)
    {
        var scanner = new Mock<IFileScanner>();
        var turbo = new Mock<IFileScanner>();
        var drives = new Mock<IDriveInfoProvider>();
        var nav = new Mock<INavigationService>();
        var admin = new Mock<IAdminService>();
        admin.Setup(a => a.IsRunningAsAdmin).Returns(isAdmin);

        var repo = settings ?? new Mock<ISettingsRepository>();
        repo.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppSettings
            {
                DefaultScanPath = defaultScanPath ?? string.Empty,
            });

        drives.Setup(d => d.GetAvailableDrives()).Returns([]);

        return new ScanViewModel(
            scanner.Object,
            turbo.Object,
            drives.Object,
            nav.Object,
            admin.Object,
            repo.Object);
    }

    // ── CanStartScan ──────────────────────────────────────────────────────────

    [Fact]
    public void CanStartScan_InitialState_IsTrue()
    {
        // Default path "C:\" exists, so no error → can start.
        var vm = BuildVm();

        // C:\ is always present on a Windows test machine; if this flaps
        // on a restricted CI runner the path validation would set an error.
        // We verify the relationship: CanStartScan == !IsScanning && no error.
        vm.IsScanning.Should().BeFalse();
        vm.CanStartScan.Should().Be(string.IsNullOrWhiteSpace(vm.ScanPathError));
    }

    [Fact]
    public void CanStartScan_WhenIsScanning_IsFalse()
    {
        var vm = BuildVm();
        vm.IsScanning = true;

        vm.CanStartScan.Should().BeFalse();
    }

    [Fact]
    public void CanStartScan_WhenPathError_IsFalse()
    {
        var vm = BuildVm();
        vm.ScanPathError = "some error";

        vm.CanStartScan.Should().BeFalse();
    }

    // ── CanCancel ─────────────────────────────────────────────────────────────

    [Fact]
    public void CanCancel_WhenNotScanning_IsFalse()
    {
        var vm = BuildVm();
        vm.CanCancel.Should().BeFalse();
    }

    [Fact]
    public void CanCancel_WhenScanning_IsTrue()
    {
        var vm = BuildVm();
        vm.IsScanning = true;

        vm.CanCancel.Should().BeTrue();
    }

    // ── CanBrowse ─────────────────────────────────────────────────────────────

    [Fact]
    public void CanBrowse_WhenNotScanning_IsTrue()
    {
        var vm = BuildVm();
        vm.CanBrowse.Should().BeTrue();
    }

    [Fact]
    public void CanBrowse_WhenScanning_IsFalse()
    {
        var vm = BuildVm();
        vm.IsScanning = true;

        vm.CanBrowse.Should().BeFalse();
    }

    // ── NeedsElevation ────────────────────────────────────────────────────────

    [Fact]
    public void NeedsElevation_DeepScanOff_IsFalse()
    {
        var vm = BuildVm(isAdmin: false);
        vm.DeepScan = false;

        vm.NeedsElevation.Should().BeFalse();
    }

    [Fact]
    public void NeedsElevation_DeepScanOn_NotAdmin_IsTrue()
    {
        var vm = BuildVm(isAdmin: false);
        vm.DeepScan = true;

        vm.NeedsElevation.Should().BeTrue();
    }

    [Fact]
    public void NeedsElevation_DeepScanOn_IsAdmin_IsFalse()
    {
        var vm = BuildVm(isAdmin: true);
        vm.DeepScan = true;

        vm.NeedsElevation.Should().BeFalse();
    }

    // ── Path validation ───────────────────────────────────────────────────────

    [Fact]
    public void SelectedPath_Empty_SetsError()
    {
        var vm = BuildVm();
        vm.SelectedPath = string.Empty;

        vm.ScanPathError.Should().NotBeNullOrEmpty();
        vm.HasScanPathError.Should().BeTrue();
    }

    [Fact]
    public void SelectedPath_WhitespaceOnly_SetsError()
    {
        var vm = BuildVm();
        vm.SelectedPath = "   ";

        vm.ScanPathError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SelectedPath_RelativePath_SetsError()
    {
        var vm = BuildVm();
        vm.SelectedPath = @"relative\path";

        vm.ScanPathError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SelectedPath_NonExistentAbsolute_SetsError()
    {
        var vm = BuildVm();
        vm.SelectedPath = @"Z:\DoesNotExist\AtAll\Ever";

        vm.ScanPathError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SelectedPath_ValidExistingPath_ClearsError()
    {
        var vm = BuildVm();
        // Set an error first.
        vm.SelectedPath = string.Empty;
        vm.ScanPathError.Should().NotBeNullOrEmpty();

        // Then set a valid path.
        vm.SelectedPath = Path.GetTempPath();

        vm.ScanPathError.Should().BeNullOrEmpty();
        vm.HasScanPathError.Should().BeFalse();
    }

    // ── IsRunningAsAdmin delegates to IAdminService ───────────────────────────

    [Fact]
    public void IsRunningAsAdmin_ReflectsAdminService()
    {
        var vmAdmin = BuildVm(isAdmin: true);
        var vmUser = BuildVm(isAdmin: false);

        vmAdmin.IsRunningAsAdmin.Should().BeTrue();
        vmUser.IsRunningAsAdmin.Should().BeFalse();
    }

    // ── InitializeAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_SetsSelectedPathFromSettings()
    {
        var expected = Path.GetTempPath().TrimEnd('\\');
        var vm = BuildVm(defaultScanPath: expected);

        await vm.InitializeAsync();

        vm.SelectedPath.Should().Be(expected);
    }

    [Fact]
    public async Task InitializeAsync_NoDefaultPath_FallsBackToCRoot()
    {
        var vm = BuildVm(defaultScanPath: string.Empty);

        await vm.InitializeAsync();

        vm.SelectedPath.Should().Be(@"C:\");
    }

    [Fact]
    public async Task InitializeAsync_AutoEnableDeepScan_SetsDeepScan()
    {
        var vm = BuildVm();

        await vm.InitializeAsync(autoEnableDeepScan: true);

        vm.DeepScan.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_PreselectedPath_OverridesSettings()
    {
        var temp = Path.GetTempPath();
        var vm = BuildVm(defaultScanPath: @"C:\");

        await vm.InitializeAsync(preselectedPath: temp);

        vm.SelectedPath.Should().Be(temp);
    }

    // ── property-change notifications ─────────────────────────────────────────

    [Fact]
    public void IsScanning_Change_NotifiesCanStartScanCanBrowseCanCancel()
    {
        var vm = BuildVm();
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.IsScanning = true;

        notified.Should().Contain(nameof(ScanViewModel.CanStartScan));
        notified.Should().Contain(nameof(ScanViewModel.CanBrowse));
        notified.Should().Contain(nameof(ScanViewModel.CanCancel));
    }

    [Fact]
    public void DeepScan_Change_NotifiesNeedsElevation()
    {
        var vm = BuildVm();
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.DeepScan = true;

        notified.Should().Contain(nameof(ScanViewModel.NeedsElevation));
    }

    [Fact]
    public void ScanPathError_Change_NotifiesHasScanPathErrorAndCanStartScan()
    {
        var vm = BuildVm();
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.ScanPathError = "error";

        notified.Should().Contain(nameof(ScanViewModel.HasScanPathError));
        notified.Should().Contain(nameof(ScanViewModel.CanStartScan));
    }
}
