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

    [Fact]
    public async Task LogResultAsync_QuarantineMoves_AreRecordedInAuditJson()
    {
        var suggestion = new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = "core.temp-files",
            Title = "Temporary files",
            Description = "Test",
            Category = CleanupCategory.TempFiles,
            Risk = CleanupRisk.Low,
            EstimatedBytes = 2048,
            TargetPaths = [@"C:\temp\a.tmp", @"C:\temp\b.tmp"],
            IsSystemPath = false,
        };
        var result = new CleanupResult
        {
            SuggestionId = suggestion.Id,
            Status = CleanupResultStatus.Success,
            BytesFreed = 2048,
            ExecutedUtc = DateTime.UtcNow,
            WasDryRun = false,
            QuarantinedPaths =
            [
                new QuarantineMove(@"C:\temp\a.tmp", @"C:\quarantine\0\temp\a.tmp"),
                new QuarantineMove(@"C:\temp\b.tmp", @"C:\quarantine\0\temp\b.tmp"),
            ],
        };

        await _repo.LogResultAsync(result, suggestion);
        var entries = await _repo.GetRecentAsync();

        entries.Should().ContainSingle();
        entries[0].AuditDataJson.Should().NotBeNull();
        using var audit = System.Text.Json.JsonDocument.Parse(entries[0].AuditDataJson!);
        var moves = audit.RootElement.GetProperty("QuarantinedFiles");
        moves.GetArrayLength().Should().Be(2);
        moves[0].GetProperty("OriginalPath").GetString().Should().Be(@"C:\temp\a.tmp");
        moves[0].GetProperty("QuarantinePath").GetString().Should().Be(@"C:\quarantine\0\temp\a.tmp");
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
