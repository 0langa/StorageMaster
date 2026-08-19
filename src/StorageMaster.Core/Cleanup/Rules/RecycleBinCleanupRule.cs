using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
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
    public string DisplayName => Loc.Get("Rule_RecycleBin_Name");
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
            Title = Loc.Format("Rule_RecycleBin_Title", info.ItemCount.ToString("N0")),
            Description = Loc.Format(
                "Safety_RecycleBin_Description",
                FormatBytes(info.SizeBytes),
                info.ItemCount.ToString("N0")),
            Category = Category,
            Risk = CleanupRisk.Medium,
            EstimatedBytes = info.SizeBytes,
            SupportsRecycleBin = false,
            SupportsQuarantine = false,
            SafetyNotes = Loc.Get("Safety_RecycleBin_Notes"),
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
