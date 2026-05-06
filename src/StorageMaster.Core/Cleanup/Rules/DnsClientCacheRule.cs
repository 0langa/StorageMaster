using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Suggests flushing the Windows DNS resolver cache by running
/// <c>ipconfig /flushdns</c> via the <c>::DnsFlush::</c> sentinel path.
///
/// Flushing the DNS cache removes stale or incorrect DNS entries that can
/// cause connectivity problems. It frees no disk space but can resolve
/// issues with outdated domain resolution.
///
/// <see cref="StorageMaster.Platform.Windows.FileDeleter"/> handles the
/// <c>::DnsFlush::</c> sentinel by spawning <c>ipconfig /flushdns</c>.
/// </summary>
public sealed class DnsClientCacheRule : ICleanupRule
{
    public string RuleId => "core.dns-cache";
    public string DisplayName => "DNS Client Cache";
    public CleanupCategory Category => CleanupCategory.DnsCache;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        // No disk space freed — the sentinel triggers ipconfig /flushdns.
        yield return new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = RuleId,
            Title = "DNS client cache",
            Description = "Flushes the Windows DNS resolver cache (runs ipconfig /flushdns). " +
                             "Removes stale domain resolution entries. No disk space freed.",
            Category = Category,
            Risk = CleanupRisk.Low,
            EstimatedBytes = 0,
            TargetPaths = ["::DnsFlush::"],
            IsSystemPath = false,
        };
    }
}
