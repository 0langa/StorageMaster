using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class ScanFilePagerTests
{
    [Fact]
    public async Task LoadAllAsync_FullFirstPage_LoadsRemainingRowsWithAGrowingPage()
    {
        var repository = new Mock<IScanRepository>();
        var repeated = MakeEntry(@"C:\data\sample.bin");
        repository.Setup(r => r.GetLargestFilesAsync(
                7,
                ScanFilePager.PageSize,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Repeat(repeated, ScanFilePager.PageSize).ToList());
        repository.Setup(r => r.SearchFilesAsync(
                7,
                null,
                null,
                "Size",
                true,
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([repeated]);

        var files = await ScanFilePager.LoadAllAsync(repository.Object, 7, CancellationToken.None);

        files.Should().HaveCount(ScanFilePager.PageSize + 1);
        repository.Verify(
            r => r.SearchFilesAsync(
                7,
                null,
                null,
                "Size",
                true,
                ScanFilePager.PageSize,
                It.Is<int>(limit => limit > ScanFilePager.PageSize),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "OFFSET paging re-reads every row before the offset, so a fixed page size " +
            "makes a full load quadratic in the number of pages");
    }

    [Fact]
    public async Task LoadAllAsync_WithPredicate_KeepsOnlyMatchesFromEveryPage()
    {
        var repository = new Mock<IScanRepository>();
        var keep = MakeEntry(@"C:\keep\wanted.bin");
        var drop = MakeEntry(@"C:\other\ignored.bin");
        var firstPage = Enumerable.Repeat(drop, ScanFilePager.PageSize - 1).Append(keep).ToList();
        repository.Setup(r => r.GetLargestFilesAsync(
                7,
                ScanFilePager.PageSize,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstPage);
        repository.Setup(r => r.SearchFilesAsync(
                7,
                null,
                null,
                "Size",
                true,
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([keep, drop]);

        var files = await ScanFilePager.LoadAllAsync(
            repository.Object,
            7,
            file => file.FullPath.StartsWith(@"C:\keep", StringComparison.OrdinalIgnoreCase),
            CancellationToken.None);

        files.Should().HaveCount(2, "one match on each of the two pages");
        files.Should().OnlyContain(file => file.FullPath == keep.FullPath);
    }

    private static FileEntry MakeEntry(string fullPath) => new()
    {
        Id = 1,
        SessionId = 7,
        FullPath = fullPath,
        FileName = Path.GetFileName(fullPath),
        Extension = Path.GetExtension(fullPath),
        SizeBytes = 1,
        CreatedUtc = DateTime.UtcNow,
        ModifiedUtc = DateTime.UtcNow,
        AccessedUtc = DateTime.UtcNow,
        Attributes = FileAttributes.Normal,
        Category = FileTypeCategory.Unknown,
    };
}
