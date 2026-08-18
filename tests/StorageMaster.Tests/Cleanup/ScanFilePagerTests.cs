using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class ScanFilePagerTests
{
    [Fact]
    public async Task LoadAllAsync_FullFirstPage_LoadsRemainingRows()
    {
        var repository = new Mock<IScanRepository>();
        var repeated = new FileEntry
        {
            Id = 1,
            SessionId = 7,
            FullPath = @"C:\data\sample.bin",
            FileName = "sample.bin",
            Extension = ".bin",
            SizeBytes = 1,
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow,
            AccessedUtc = DateTime.UtcNow,
            Attributes = FileAttributes.Normal,
            Category = FileTypeCategory.Unknown,
        };
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
                ScanFilePager.PageSize,
                ScanFilePager.PageSize,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([repeated]);

        var files = await ScanFilePager.LoadAllAsync(repository.Object, 7, CancellationToken.None);

        files.Should().HaveCount(ScanFilePager.PageSize + 1);
    }
}
