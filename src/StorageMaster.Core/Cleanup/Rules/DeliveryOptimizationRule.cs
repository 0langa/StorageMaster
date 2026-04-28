using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Targets the Windows Delivery Optimization cache — the peer-to-peer update
/// sharing cache that Windows maintains to help other PCs on your network or
/// the internet download updates faster. Can grow very large unnoticed.
/// </summary>
public sealed class DeliveryOptimizationRule : ICleanupRule
{
    public string RuleId      => "core.delivery-optimization";
    public string DisplayName => "Delivery Optimization Cache";
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
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        long totalBytes = 0;
        var  paths      = new List<string>();

        foreach (var dir in CandidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir)) continue;
            try
            {
                long size = Directory
                    .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
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
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Delivery Optimization cache ({FormatBytes(totalBytes)})",
            Description    = "Windows stores downloaded update content here to share with other " +
                             "devices. Deleting it does not affect your installed updates; Windows " +
                             "will rebuild the cache over time as needed.",
            Category       = Category,
            Risk           = CleanupRisk.Low,
            EstimatedBytes = totalBytes,
            TargetPaths    = paths,
            IsSystemPath   = true,
        };
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (1L << 20):F1} MB",
        >= 1L << 10 => $"{b / (1L << 10):F1} KB",
        _           => $"{b} B",
    };
}
