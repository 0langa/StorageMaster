using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Scanner;
using StorageMaster.Platform.Windows;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Scanner;

/// <summary>
/// A file with several hard links is one file on disk, so it counts once in folder,
/// category and session totals. Every name is still listed with its real size.
/// <para>
/// The scanner test creates real hard links, inside a temp folder it owns, so the
/// identities come from Windows rather than from a fixture.
/// </para>
/// </summary>
public sealed class HardLinkSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sm_hardlink_{Guid.NewGuid():N}");

    public HardLinkSizeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Tracker_CountsAFileOncePerVolumeAndAlwaysCountsUnknownIdentities()
    {
        var tracker = new HardLinkTracker();

        tracker.IsFirstLink(new FileIdentity("VOL1", 7)).Should().BeTrue();
        tracker.IsFirstLink(new FileIdentity("vol1", 7)).Should().BeFalse("the same file reached through another name");
        tracker.IsFirstLink(new FileIdentity("VOL2", 7)).Should().BeTrue("the same index on another volume is another file");
        tracker.IsFirstLink(new FileIdentity("VOL1", 8)).Should().BeTrue();
        tracker.IsFirstLink(null).Should().BeTrue();
        tracker.IsFirstLink(null).Should().BeTrue("without an identity nothing can be proven to be shared");
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public void Tracker_NeverTreatsAPlaceholderFileIdAsAHardLink(ulong placeholder)
    {
        // ReFS reports all ones for a file whose 128-bit id does not fit in 64 bits,
        // and some network and user-mode file systems report 0 for every file.
        // Unrelated files share those values; folding them would drop all but the
        // first file from the totals.
        var tracker = new HardLinkTracker();

        tracker.IsFirstLink(new FileIdentity("VOL1", placeholder)).Should().BeTrue();
        tracker.IsFirstLink(new FileIdentity("VOL1", placeholder)).Should().BeTrue(
            "a placeholder id is shared by unrelated files and proves nothing");
        tracker.IsFirstLink(new FileIdentity("VOL1", 5)).Should().BeTrue();
        tracker.IsFirstLink(new FileIdentity("VOL1", 5)).Should().BeFalse("real ids are still tracked");
    }

    [Fact]
    public async Task ManagedScan_CountsAHardLinkedFileOnce()
    {
        var original = Path.Combine(_root, "original.bin");
        File.WriteAllBytes(original, new byte[4096]);
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        var link = Path.Combine(sub, "link.bin");
        CreateHardLinkOrFail(link, original);
        File.WriteAllBytes(Path.Combine(sub, "other.bin"), new byte[100]);

        var files = new List<FileEntry>();
        var folders = new List<FolderEntry>();
        IReadOnlyDictionary<string, long>? totals = null;
        var repo = CapturingRepository(files, folders, captured => totals = captured);
        var scanner = new FileScanner(repo.Object, NullLogger<FileScanner>.Instance, new FileIdentityProvider());

        var session = await scanner.ScanAsync(
            new ScanOptions { RootPath = _root, MaxParallelism = 1 },
            new Progress<ScanProgress>());

        session.Status.Should().Be(ScanStatus.Completed);
        session.TotalFiles.Should().Be(3, "every name is still listed");
        session.TotalSizeBytes.Should().Be(4096 + 100, "the linked data exists on disk once");
        files.Should().OnlyContain(entry => entry.SizeBytes == new FileInfo(entry.FullPath).Length,
            "each entry keeps the file's real size");
        files.Count(static entry => entry.IsAdditionalHardLink).Should().Be(1);
        files.Where(static entry => entry.FileName != "other.bin")
            .Should().ContainSingle(static entry => !entry.IsAdditionalHardLink);
        folders.Sum(static folder => folder.DirectSizeBytes).Should().Be(4096 + 100);
        totals.Should().NotBeNull();
        totals![ScanOptionValidator.NormalizeDirectoryPath(_root)].Should().Be(4096 + 100);
    }

    [Fact]
    public async Task CategoryBreakdown_CountsAdditionalHardLinksOnce()
    {
        var databasePath = Path.Combine(_root, "hardlinks.db");
        await using var db = new StorageDbContext(databasePath, NullLogger<StorageDbContext>.Instance);
        var repository = new ScanRepository(db);
        var session = await repository.CreateSessionAsync(_root);

        await repository.InsertFileEntriesAsync(
        [
            Entry(session.Id, @"C:\data\a.jpg", 1000, new FileIdentity("VOL1", 1), additional: false),
            Entry(session.Id, @"C:\data\copy\a.jpg", 1000, new FileIdentity("VOL1", 1), additional: true),
            Entry(session.Id, @"C:\data\b.jpg", 10, new FileIdentity("VOL1", 2), additional: false),
        ]);

        var breakdown = await repository.GetCategoryBreakdownAsync(session.Id);

        breakdown[FileTypeCategory.Image].Should().Be((3L, 1010L),
            "both names are counted as files, but the shared data only once");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static FileEntry Entry(long sessionId, string path, long size, FileIdentity identity, bool additional) => new()
    {
        Id = 0,
        SessionId = sessionId,
        FullPath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        SizeBytes = size,
        CreatedUtc = DateTime.UtcNow,
        ModifiedUtc = DateTime.UtcNow,
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Archive,
        Category = FileTypeCategory.Image,
        Identity = identity,
        IsAdditionalHardLink = additional,
    };

    private static Mock<IScanRepository> CapturingRepository(
        List<FileEntry> files,
        List<FolderEntry> folders,
        Action<IReadOnlyDictionary<string, long>> onTotals)
    {
        var repo = new Mock<IScanRepository>();
        repo.Setup(r => r.CreateSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanSession { Id = 1, RootPath = @"C:\", StartedUtc = DateTime.UtcNow, Status = ScanStatus.Running });
        repo.Setup(r => r.UpdateSessionAsync(It.IsAny<ScanSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.InsertFileEntriesAsync(It.IsAny<IReadOnlyList<FileEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FileEntry>, CancellationToken>((batch, _) =>
            {
                lock (files)
                    files.AddRange(batch);
            })
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.UpsertFolderEntriesAsync(It.IsAny<IReadOnlyList<FolderEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<FolderEntry>, CancellationToken>((batch, _) =>
            {
                lock (folders)
                    folders.AddRange(batch);
            })
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.GetAllFolderPathsForSessionAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (IReadOnlyList<FolderEntry>)folders.ToArray());
        repo.Setup(r => r.UpdateFolderTotalsAsync(
                It.IsAny<long>(),
                It.IsAny<IReadOnlyDictionary<string, long>>(),
                It.IsAny<CancellationToken>()))
            .Callback<long, IReadOnlyDictionary<string, long>, CancellationToken>((_, captured, _) => onTotals(captured))
            .Returns(Task.CompletedTask);
        return repo;
    }

    private static void CreateHardLinkOrFail(string link, string existing)
    {
        if (!CreateHardLink(link, existing, IntPtr.Zero))
            throw new IOException($"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
