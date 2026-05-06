using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

public sealed class ScanErrorRepositoryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly ScanRepository _scanRepository;
    private readonly ScanErrorRepository _repo;

    public ScanErrorRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"scan_errors_{Guid.NewGuid():N}.db");
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _scanRepository = new ScanRepository(_ctx);
        _repo = new ScanErrorRepository(_ctx);
    }

    [Fact]
    public async Task GetErrorsPageForSessionAsync_ReturnsPagedNewestFirstResults()
    {
        var session = await _scanRepository.CreateSessionAsync(@"C:\Root");
        await _repo.LogErrorsAsync(session.Id, [
            new ScanError { Id = 0, SessionId = session.Id, Path = @"C:\Root\a", ErrorType = "Denied", Message = "1", OccurredAt = DateTime.UtcNow.AddMinutes(-3) },
            new ScanError { Id = 0, SessionId = session.Id, Path = @"C:\Root\b", ErrorType = "Denied", Message = "2", OccurredAt = DateTime.UtcNow.AddMinutes(-2) },
            new ScanError { Id = 0, SessionId = session.Id, Path = @"C:\Root\c", ErrorType = "Denied", Message = "3", OccurredAt = DateTime.UtcNow.AddMinutes(-1) },
        ]);

        var firstPage = await _repo.GetErrorsPageForSessionAsync(session.Id, 0, 2);
        var total = await _repo.CountErrorsForSessionAsync(session.Id);

        total.Should().Be(3);
        firstPage.Should().HaveCount(2);
        firstPage[0].Path.Should().Be(@"C:\Root\c");
        firstPage[1].Path.Should().Be(@"C:\Root\b");
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
