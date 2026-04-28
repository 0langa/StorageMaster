using System.Runtime.CompilerServices;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Cleanup.Rules;

/// <summary>
/// Detects leftover AppData folders from programs that are no longer installed.
///
/// Algorithm:
///   1. Collect all installed program display-names via IInstalledProgramProvider.
///   2. Build a set of "known" folder-name tokens from those names.
///   3. Walk the top level of %LOCALAPPDATA% and %APPDATA%, flagging sub-folders
///      whose name does not fuzzy-match any known program AND that haven't been
///      modified in the past 90 days (reduces false positives on active software).
///   4. Also checks %ProgramData% for orphaned vendor directories.
///
/// Risk: Medium — heuristic matching can produce false positives. Suggestions
/// are shown individually so the user can review before acting.
/// </summary>
public sealed class UninstalledProgramLeftoversRule : ICleanupRule
{
    private readonly IInstalledProgramProvider _programProvider;

    public string RuleId      => "core.program-leftovers";
    public string DisplayName => "Uninstalled Program Leftovers";
    public CleanupCategory Category => CleanupCategory.ProgramLeftovers;

    // Folders that exist in AppData for system / framework reasons and must
    // never be treated as leftovers regardless of name matching.
    private static readonly HashSet<string> SafelistFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "Temp", "Local", "Roaming",
        "SystemApps", "ConnectedDevicesPlatform", "DBStore", "INetCache",
        "INetCookies", "History", "ElevatedDiagnostics", "GameDVR",
        ".NET", "dotnet", "Mono", "Java", "Python", "node_modules",
    };

    public UninstalledProgramLeftoversRule(IInstalledProgramProvider programProvider)
        => _programProvider = programProvider;

    public async IAsyncEnumerable<CleanupSuggestion> AnalyzeAsync(
        long              sessionId,
        AppSettings       settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        // Build token set from installed program names for fast fuzzy lookup.
        var installed = _programProvider.GetInstalledPrograms();
        var knownTokens = BuildKnownTokens(installed);

        var local      = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var cutoff = DateTime.UtcNow.AddDays(-90);

        var candidates = new List<(string path, long size, string name)>();

        foreach (var root in new[] { local, roaming, commonData })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var dir in subDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(dir);
                if (SafelistFolderNames.Contains(folderName)) continue;

                // Skip recently-modified folders — likely still in use.
                try
                {
                    var lastWrite = Directory.GetLastWriteTimeUtc(dir);
                    if (lastWrite > cutoff) continue;
                }
                catch { continue; }

                // Check if this folder's name matches any known installed program.
                if (IsKnownProgram(folderName, knownTokens)) continue;

                // Compute size (best-effort).
                long size = 0;
                try
                {
                    size = Directory
                        .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                }
                catch { /* skip inaccessible */ }

                // Only surface folders with at least 10 MB — smaller ones aren't worth the noise.
                if (size < 10L * 1024 * 1024) continue;

                candidates.Add((dir, size, folderName));
            }
        }

        if (candidates.Count == 0) yield break;

        long totalBytes = candidates.Sum(c => c.size);

        yield return new CleanupSuggestion
        {
            Id             = Guid.NewGuid(),
            RuleId         = RuleId,
            Title          = $"Uninstalled program leftovers ({candidates.Count} folder(s))",
            Description    = $"AppData folders that appear to belong to programs no longer installed, " +
                             $"unmodified for over 90 days. Review carefully before deleting. " +
                             $"Estimated savings: {FormatBytes(totalBytes)}.",
            Category       = Category,
            Risk           = CleanupRisk.Medium,
            EstimatedBytes = totalBytes,
            TargetPaths    = candidates.Select(c => c.path).ToList(),
            IsSystemPath   = false,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HashSet<string> BuildKnownTokens(IReadOnlyList<InstalledProgramInfo> programs)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prog in programs)
        {
            // Add the full name and significant sub-words.
            tokens.Add(prog.DisplayName);
            foreach (var word in Tokenize(prog.DisplayName))
                if (word.Length >= 4) tokens.Add(word);

            if (prog.Publisher is not null)
            {
                tokens.Add(prog.Publisher);
                foreach (var word in Tokenize(prog.Publisher))
                    if (word.Length >= 4) tokens.Add(word);
            }

            if (prog.InstallLocation is not null)
            {
                // The last component of the install path is usually the program folder name.
                var last = Path.GetFileName(prog.InstallLocation.TrimEnd('\\', '/'));
                if (!string.IsNullOrWhiteSpace(last)) tokens.Add(last);
            }
        }
        return tokens;
    }

    private static bool IsKnownProgram(string folderName, HashSet<string> tokens)
    {
        if (tokens.Contains(folderName)) return true;
        // Partial match: if the folder name contains a known token as a substring.
        foreach (var token in tokens)
            if (folderName.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IEnumerable<string> Tokenize(string name) =>
        name.Split([' ', '-', '_', '.', '(', ')', ',', '&'], StringSplitOptions.RemoveEmptyEntries);

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (1L << 20):F1} MB",
        >= 1L << 10 => $"{b / (1L << 10):F1} KB",
        _           => $"{b} B",
    };
}
