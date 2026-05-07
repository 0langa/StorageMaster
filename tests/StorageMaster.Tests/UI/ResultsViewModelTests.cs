using FluentAssertions;
using Moq;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.UI.Infrastructure;
using StorageMaster.UI.Pages;

namespace StorageMaster.Tests.UI;

/// <summary>
/// Unit tests for ResultsViewModel: session loading, state management,
/// background-work cancellation, and computed properties.
/// Runs on background thread — DispatcherQueue is passed as null.
/// </summary>
public sealed class ResultsViewModelTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static ResultsViewModel BuildVm(
        Mock<IScanRepository>? repo = null,
        Mock<IScanErrorRepository>? errorRepo = null)
    {
        var r = repo ?? new Mock<IScanRepository>();
        var er = errorRepo ?? new Mock<IScanErrorRepository>();
        var del = new Mock<IScanResultDeletionService>();
        var nav = new Mock<INavigationService>();
        var dlg = new Mock<IDialogService>();

        // Pass null for DispatcherQueue — safe as long as FilterText is not changed.
        return new ResultsViewModel(r.Object, er.Object, del.Object, nav.Object, dlg.Object,
            dispatcherQueue: null);
    }

    private static ScanSession CompletedSession(long id, string root = @"C:\") => new()
    {
        Id = id,
        RootPath = root,
        Status = ScanStatus.Completed,
        StartedUtc = DateTime.UtcNow.AddMinutes(-5),
        CompletedUtc = DateTime.UtcNow,
        TotalFiles = 100,
        TotalFolders = 10,
        TotalSizeBytes = 1_000_000,
    };

    /// <summary>Configures all repo methods touched during a full LoadAsync call.</summary>
    private static void SetupForLoad(Mock<IScanRepository> repo, long sessionId, ScanSession session,
        Mock<IScanErrorRepository>? errorRepo = null)
    {
        repo.Setup(r => r.GetSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        repo.Setup(r => r.SearchFilesAsync(sessionId,
                It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<FileEntry>)[]);
        repo.Setup(r => r.CountFilesAsync(sessionId,
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);
        repo.Setup(r => r.SearchFoldersAsync(sessionId,
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<FolderEntry>)[]);
        repo.Setup(r => r.CountFoldersAsync(sessionId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);
        repo.Setup(r => r.GetCategoryBreakdownAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<FileTypeCategory, (long Count, long Bytes)>)
                new Dictionary<FileTypeCategory, (long Count, long Bytes)>());
        repo.Setup(r => r.GetLargestFilesAsync(sessionId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<FileEntry>)[]);
        repo.Setup(r => r.GetLargestFoldersAsync(sessionId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<FolderEntry>)[]);
    }

    // ── initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void InitialState_HasSessionFalse_NoFiles()
    {
        var vm = BuildVm();

        vm.HasSession.Should().BeFalse();
        vm.LargestFiles.Should().BeEmpty();
        vm.LargestFolders.Should().BeEmpty();
        vm.CategoryBreakdown.Should().BeEmpty();
    }

    // ── LoadAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_ValidSession_SetsHasSessionTrue()
    {
        var session = CompletedSession(42, @"C:\Users\Test");
        var repo = new Mock<IScanRepository>();
        SetupForLoad(repo, 42, session);

        var errorRepo = new Mock<IScanErrorRepository>();
        errorRepo.Setup(r => r.CountErrorsForSessionAsync(42, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(0L);

        var vm = BuildVm(repo: repo, errorRepo: errorRepo);
        await vm.LoadAsync(42);

        vm.HasSession.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_ZeroSessionId_ResetsToNoSession()
    {
        var vm = BuildVm();
        await vm.LoadAsync(0);

        vm.HasSession.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_NegativeSessionId_ResetsToNoSession()
    {
        var vm = BuildVm();
        await vm.LoadAsync(-5);

        vm.HasSession.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_SameSessionIdTwice_DoesNotReloadSecondTime()
    {
        var session = CompletedSession(7);
        var repo = new Mock<IScanRepository>();
        SetupForLoad(repo, 7, session);

        var errorRepo = new Mock<IScanErrorRepository>();
        errorRepo.Setup(r => r.CountErrorsForSessionAsync(7, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(0L);

        var vm = BuildVm(repo: repo, errorRepo: errorRepo);
        await vm.LoadAsync(7);
        await vm.LoadAsync(7); // second call should be a no-op

        repo.Verify(r => r.GetSessionAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── LoadMostRecentAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task LoadMostRecentAsync_NoCompletedSession_HasSessionFalse()
    {
        var repo = new Mock<IScanRepository>();
        repo.Setup(r => r.GetRecentSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var vm = BuildVm(repo: repo);
        await vm.LoadMostRecentAsync();

        vm.HasSession.Should().BeFalse();
    }

    [Fact]
    public async Task LoadMostRecentAsync_WithCompletedSession_LoadsIt()
    {
        var session = CompletedSession(55);
        var repo = new Mock<IScanRepository>();
        repo.Setup(r => r.GetRecentSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([session]);
        SetupForLoad(repo, 55, session);

        var errorRepo = new Mock<IScanErrorRepository>();
        errorRepo.Setup(r => r.CountErrorsForSessionAsync(55, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(0L);

        var vm = BuildVm(repo: repo, errorRepo: errorRepo);
        await vm.LoadMostRecentAsync();

        vm.HasSession.Should().BeTrue();
    }

    [Fact]
    public async Task LoadMostRecentAsync_OnlyInProgressSessions_HasSessionFalse()
    {
        var inProgress = new ScanSession
        {
            Id = 11,
            RootPath = @"C:\",
            Status = ScanStatus.Running,
            StartedUtc = DateTime.UtcNow,
        };
        var repo = new Mock<IScanRepository>();
        repo.Setup(r => r.GetRecentSessionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([inProgress]);

        var vm = BuildVm(repo: repo);
        await vm.LoadMostRecentAsync();

        vm.HasSession.Should().BeFalse();
    }

    // ── CancelBackgroundWork ──────────────────────────────────────────────────

    [Fact]
    public void CancelBackgroundWork_WhenNoWorkRunning_DoesNotThrow()
    {
        var vm = BuildVm();
        var act = () => vm.CancelBackgroundWork();

        act.Should().NotThrow();
    }

    // ── CategoryFilterApplied event ───────────────────────────────────────────

    [Fact]
    public void CategoryFilterApplied_CanSubscribe()
    {
        var vm = BuildVm();
        var fired = false;
        vm.CategoryFilterApplied += (_, _) => fired = true;

        // Event is fired by internal command; just verify subscription works.
        fired.Should().BeFalse();
    }

    // ── computed properties ───────────────────────────────────────────────────

    [Fact]
    public void HasErrors_NoErrors_IsFalse()
    {
        var vm = BuildVm();
        vm.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void HasCategoryFilter_Empty_IsFalse()
    {
        var vm = BuildVm();
        vm.SelectedCategoryFilter = string.Empty;

        vm.HasCategoryFilter.Should().BeFalse();
    }

    [Fact]
    public void HasCategoryFilter_NonEmpty_IsTrue()
    {
        var vm = BuildVm();
        vm.SelectedCategoryFilter = "Images";

        vm.HasCategoryFilter.Should().BeTrue();
    }

    [Fact]
    public void HasSessionNote_Empty_IsFalse()
    {
        var vm = BuildVm();
        vm.SessionNote = string.Empty;

        vm.HasSessionNote.Should().BeFalse();
    }

    [Fact]
    public void HasSessionNote_WithNote_IsTrue()
    {
        var vm = BuildVm();
        vm.SessionNote = "Test scan run";

        vm.HasSessionNote.Should().BeTrue();
    }
}
