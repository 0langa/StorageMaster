using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Localization;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Targets the Windows Delivery Optimization cache — the peer-to-peer update
/// sharing cache that Windows maintains to help other PCs on your network or
/// the internet download updates faster. Can grow very large unnoticed.
/// </summary>
public sealed class DeliveryOptimizationRule : ICleanupRule
{
    public string RuleId => "core.delivery-optimization";
    public string DisplayName => Loc.Get("Rule_DeliveryOptimization_Name");
    public CleanupCategory Category => CleanupCategory.DeliveryOptimization;

    // The DO cache lives in %WINDIR%\SoftwareDistribution\DeliveryOptimization
    // and sometimes in %SYSTEMDRIVE%\Windows\ServiceProfiles\NetworkService\AppData\Local\Packages\...
    private static readonly string[] CandidatePaths =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SoftwareDistribution", "DeliveryOptimization"),
        Path.Combine(@"C:\Windows", "SoftwareDistribution", "DeliveryOptimization"),
    ];

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long sessionId,
        AppSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        long totalBytes = 0;
        var paths = new List<string>();

        foreach (var dir in CandidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir)) continue;
            try
            {
                // Sizes come from the enumeration itself — the string overload would
                // discard them and re-stat every file.
                long size = new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(file => { try { return file.Length; } catch { return 0L; } });
                if (size == 0) continue;
                totalBytes += size;
                if (!paths.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    paths.Add(dir);
            }
            catch { /* needs admin for full access */ }
        }

        if (paths.Count == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id = Guid.NewGuid(),
            RuleId = RuleId,
            Title = Loc.Format("Rule_DeliveryOptimization_Title", FormatBytes(totalBytes)),
            Description = Loc.Get("Rule_DeliveryOptimization_Description"),
            Category = Category,
            Risk = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths = paths,
            IsSystemPath = true,
        };
    }

    private static string FormatBytes(long b) => ByteFormat.Format(b);
}
