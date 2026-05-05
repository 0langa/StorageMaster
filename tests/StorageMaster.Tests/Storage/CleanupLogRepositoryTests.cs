using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Models;
using StorageMaster.Storage;
using StorageMaster.Storage.Repositories;

namespace StorageMaster.Tests.Storage;

public sealed class CleanupLogRepositoryTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly StorageDbContext _ctx;
    private readonly CleanupLogRepository _repo;

    public CleanupLogRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cleanup_log_{Guid.NewGuid():N}.db");
        _ctx = new StorageDbContext(_dbPath, NullLogger<StorageDbContext>.Instance);
        _repo = new CleanupLogRepository(_ctx);
    }

    [Fact]
    public async Task LogResultAsync_PersistsAuditMetadata()
    {
        var suggestion = new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = "duplicates.cleanup",
            Title = "Duplicate group",
            Description = "Test",
            Category = CleanupCategory.DuplicateFiles,
            Risk = CleanupRisk.Low,
            EstimatedBytes = 1024,
            TargetPaths = [@"C:\temp\a.txt"],
            IsSystemPath = false,
            AuditDataJson = "{\"DuplicateGroupId\":12}",
        };
        var result = new CleanupResult
        {
            SuggestionId = suggestion.Id,
            Status = CleanupResultStatus.Success,
            BytesFreed = 1024,
            ExecutedUtc = DateTime.UtcNow,
            WasDryRun = false,
        };

        await _repo.LogResultAsync(result, suggestion);
        var entries = await _repo.GetRecentAsync();

        entries.Should().ContainSingle();
        entries[0].RuleId.Should().Be("duplicates.cleanup");
        entries[0].AuditDataJson.Should().Be("{\"DuplicateGroupId\":12}");
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
