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

    public string RuleId      => "core.recycle-bin";
    public string DisplayName => "Recycle Bin";
    public CleanupCategory Category => CleanupCategory.RecycleBin;

    public RecycleBinCleanupRule(IRecycleBinInfoProvider recycleBin) => _recycleBin = recycleBin;

#pragma warning disable CS1998 // Iterator with no awaits — acceptable for this synchronous rule.
    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var info = _recycleBin.GetRecycleBinInfo();
        if (info.SizeBytes <= 0) yield break;

        yield return new CleanupSuggestion
        {
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Recycle Bin ({info.ItemCount:N0} items)",
            Description    = $"Recycle Bin currently holds {FormatBytes(info.SizeBytes)} across {info.ItemCount:N0} items. " +
                             "Emptying it is safe and permanent.",
            Category       = Category,
            Risk           = CleanupRisk.Safe,
            EstimatedBytes = info.SizeBytes,
            // Sentinel value — the deleter recognises this and calls SHEmptyRecycleBin.
            TargetPaths    = ["::RecycleBin::"],
            IsSystemPath   = false,
        };
    }
#pragma warning restore CS1998

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1L << 30 => $"{bytes / (1L << 30):F1} GB",
            >= 1L << 20 => $"{bytes / (1L << 20):F1} MB",
            >= 1L << 10 => $"{bytes / (1L << 10):F1} KB",
            _           => $"{bytes} B",
        };
}

/// <summary>Platform abstraction for querying Recycle Bin metadata.</summary>
public interface IRecycleBinInfoProvider
{
    RecycleBinInfo GetRecycleBinInfo();
}

public sealed record RecycleBinInfo(long SizeBytes, int ItemCount);
