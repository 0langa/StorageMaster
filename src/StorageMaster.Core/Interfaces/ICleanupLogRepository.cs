using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>Audit log for every cleanup action — append-only, never deleted by the app.</summary>
public interface ICleanupLogRepository
{
    Task LogResultAsync(CleanupResult result, CleanupSuggestion suggestion, CancellationToken ct = default);
    Task<IReadOnlyList<CleanupLogEntry>> GetRecentAsync(int count = 50, CancellationToken ct = default);
}

public sealed record CleanupLogEntry
{
    public required long     Id             { get; init; }
    public required Guid     SuggestionId   { get; init; }
    public required string   RuleId         { get; init; }
    public required string   Title          { get; init; }
    public required long     BytesFreed     { get; init; }
    public required bool     WasDryRun      { get; init; }
    public required string   Status         { get; init; }
    public required DateTime ExecutedUtc    { get; init; }
    public string?           ErrorMessage   { get; init; }
}
