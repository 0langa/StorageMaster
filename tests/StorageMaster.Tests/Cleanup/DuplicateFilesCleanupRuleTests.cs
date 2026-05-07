using System.Text.Json;
using FluentAssertions;
using Moq;
using StorageMaster.Core.Cleanup.Rules;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Tests.Cleanup;

public sealed class DuplicateFilesCleanupRuleTests
{
    [Fact]
    public async Task AnalyzeAsync_UsesLatestCompletedRunAndOnlySelectedExistingCopies()
    {
        // The rule now checks File.Exists(keeper.FullPath) to prevent data loss,
        // so the keeper must be a real file on disk.
        var keeperPath = Path.Combine(Path.GetTempPath(), $"smdup_keeper_{Guid.NewGuid():N}.txt");
        File.WriteAllText(keeperPath, "keeper content");
        try
        {
            var repo = new Mock<IDuplicateRepository>();
            repo.Setup(r => r.GetRunsForSessionAsync(7, It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new DuplicateRun
                    {
                        Id = 11,
                        SessionId = 7,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-10),
                        CompletedUtc = DateTime.UtcNow.AddMinutes(-9),
                        Status = DuplicateRunStatus.Completed,
                        ConfigJson = "{}",
                        GroupCount = 1,
                    },
                    new DuplicateRun
                    {
                        Id = 10,
                        SessionId = 7,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-30),
                        CompletedUtc = DateTime.UtcNow.AddMinutes(-29),
                        Status = DuplicateRunStatus.Completed,
                        ConfigJson = "{}",
                        GroupCount = 99,
                    },
                ]);
            repo.Setup(r => r.GetGroupsForRunAsync(11, It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new DuplicateGroup
                    {
                        Id = 21,
                        RunId = 11,
                        Method = DuplicateMethod.NormalizedText,
                        Algorithm = "TEXT-NORM-SHA256",
                        Confidence = 0.8,
                        TotalBytes = 900,
                        ReclaimableBytes = 600,
                        RepresentativeFileEntryId = 1,
                    },
                ]);
            repo.Setup(r => r.GetMembersForGroupAsync(21, It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new DuplicateGroupMember
                    {
                        Id = 1,
                        GroupId = 21,
                        FileEntryId = 1,
                        FullPath = keeperPath,   // real file — File.Exists check passes
                        FileName = Path.GetFileName(keeperPath),
                        SizeBytes = 300,
                        ModifiedUtc = DateTime.UtcNow,
                        Score = 0.8,
                        IsKeeper = true,
                        IsSelected = false,
                        RecommendationReason = "Kept",
                        ExistsNow = true,
                    },
                    new DuplicateGroupMember
                    {
                        Id = 2,
                        GroupId = 21,
                        FileEntryId = 2,
                        FullPath = @"C:\docs\copy-a.txt",
                        FileName = "copy-a.txt",
                        SizeBytes = 300,
                        ModifiedUtc = DateTime.UtcNow,
                        Score = 0.8,
                        IsKeeper = false,
                        IsSelected = true,
                        RecommendationReason = "Duplicate",
                        ExistsNow = true,
                    },
                    new DuplicateGroupMember
                    {
                        Id = 3,
                        GroupId = 21,
                        FileEntryId = 3,
                        FullPath = @"C:\docs\copy-b.txt",
                        FileName = "copy-b.txt",
                        SizeBytes = 300,
                        ModifiedUtc = DateTime.UtcNow,
                        Score = 0.8,
                        IsKeeper = false,
                        IsSelected = true,
                        RecommendationReason = "Duplicate",
                        ExistsNow = false,
                    },
                ]);

            var rule = new DuplicateFilesCleanupRule(repo.Object);
            var suggestions = new List<CleanupSuggestion>();
            await foreach (var suggestion in rule.AnalyzeAsync(7, new AppSettings()))
                suggestions.Add(suggestion);

            suggestions.Should().ContainSingle();
            suggestions[0].EstimatedBytes.Should().Be(300);
            suggestions[0].TargetPaths.Should().Equal([@"C:\docs\copy-a.txt"]);
            suggestions[0].Category.Should().Be(CleanupCategory.DuplicateFiles);

            using var audit = JsonDocument.Parse(suggestions[0].AuditDataJson!);
            audit.RootElement.GetProperty("DuplicateRunId").GetInt64().Should().Be(11);
            audit.RootElement.GetProperty("DuplicateGroupId").GetInt64().Should().Be(21);
            audit.RootElement.GetProperty("KeeperPath").GetString().Should().Be(keeperPath);
        }
        finally
        {
            if (File.Exists(keeperPath)) File.Delete(keeperPath);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_NoCompletedRun_YieldsNothing()
    {
        var repo = new Mock<IDuplicateRepository>();
        repo.Setup(r => r.GetRunsForSessionAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DuplicateRun
                {
                    Id = 1,
                    SessionId = 7,
                    StartedUtc = DateTime.UtcNow,
                    Status = DuplicateRunStatus.Failed,
                    ConfigJson = "{}",
                },
            ]);

        var rule = new DuplicateFilesCleanupRule(repo.Object);
        var suggestions = new List<CleanupSuggestion>();
        await foreach (var suggestion in rule.AnalyzeAsync(7, new AppSettings()))
            suggestions.Add(suggestion);

        suggestions.Should().BeEmpty();
    }
}
