using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Reports the current Recycle Bin size and suggests emptying it.
/// The actual size query is delegated to the platform layer via IRecycleBinInfoProvider,
/// which may not be available on all platforms (stubbed gracefully).
/// </summary>
public sealed class RecycleBinCleanupRule : ICleanupRule
{
    private readonly IRecycleBinInfoProvider _recycleBin;

    public string RuleId => "core.recycle-bin";
    public string DisplayName => "Recycle Bin";
    public CleanupCategory Category => CleanupCategory.RecycleBin;

    public RecycleBinCleanupRule(IRecycleBinInfoProvider recycleBin) => _recycleBin = recycleBin;

#pragma warning disable CS1998 // Iterator with no awaits — acceptable for this synchronous rule.
    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var info = _recycleBin.GetRecycleBinInfo();
        if (info.SizeBytes <= 0) yield break;

        yield return new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = RuleId,
            Title = $"Recycle Bin ({info.ItemCount:N0} items)",
            Description = $"Recycle Bin currently holds {FormatBytes(info.SizeBytes)} across {info.ItemCount:N0} items. " +
                             "Emptying it is irreversible.",
            Category = Category,
            Risk = CleanupRisk.Medium,
            EstimatedBytes = info.SizeBytes,
            SupportsRecycleBin = false,
            SupportsQuarantine = false,
            SafetyNotes = "Permanent deletion only. Emptying the Recycle Bin cannot be undone.",
            // Sentinel value — the deleter recognises this and calls SHEmptyRecycleBin.
            TargetPaths = ["::RecycleBin::"],
            IsSystemPath = false,
        };
    }
#pragma warning restore CS1998

    private static string FormatBytes(long bytes) => ByteFormat.Format(bytes);
}

/// <summary>Platform abstraction for querying Recycle Bin metadata.</summary>
public interface IRecycleBinInfoProvider
{
    RecycleBinInfo GetRecycleBinInfo();
}

public sealed record RecycleBinInfo(long SizeBytes, int ItemCount);
