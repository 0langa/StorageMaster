using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Targets Windows Error Reporting (WER) crash dumps and diagnostic logs.
/// These accumulate silently over time and can consume several gigabytes.
/// Safe to delete — Microsoft has already received the reports if you
/// chose to send them; local copies serve no further purpose.
/// </summary>
public sealed class WindowsErrorReportingRule : ICleanupRule
{
    public string RuleId      => "core.windows-error-reporting";
    public string DisplayName => "Windows Error Reports";
    public CleanupCategory Category => CleanupCategory.WindowsErrorReporting;

    private static IEnumerable<string> GetCandidatePaths()
    {
        var local  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        // Per-user WER
        var userWer = Path.Combine(local, "Microsoft", "Windows", "WER");
        if (Directory.Exists(userWer)) yield return userWer;

        // Machine-wide WER
        var systemWer = Path.Combine(programData, "Microsoft", "Windows", "WER");
        if (Directory.Exists(systemWer)) yield return systemWer;

        // CrashDumps folder (Windows 10/11)
        var crashDumps = Path.Combine(local, "CrashDumps");
        if (Directory.Exists(crashDumps)) yield return crashDumps;

        // Windows memory dumps in root of Windows directory
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var dump in Directory.EnumerateFiles(winDir, "*.dmp", SearchOption.TopDirectoryOnly))
            yield return dump;
    }

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        long totalBytes = 0;
        var  paths      = new List<string>();

        foreach (var path in GetCandidatePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                long size;
                if (File.Exists(path))
                {
                    size = new FileInfo(path).Length;
                }
                else
                {
                    size = Directory
                        .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                }
                if (size == 0) continue;
                totalBytes += size;
                paths.Add(path);
            }
            catch { /* best-effort */ }
        }

        if (paths.Count == 0) yield break;

        yield return new CleanupSuggestion
        {
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Windows Error Reports ({paths.Count} location(s))",
            Description    = "Crash dumps and diagnostic reports generated when programs or Windows " +
                             "itself encounters an error. Safe to delete — Microsoft has already " +
                             $"received any reports you chose to send. Estimated savings: {FormatBytes(totalBytes)}.",
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
