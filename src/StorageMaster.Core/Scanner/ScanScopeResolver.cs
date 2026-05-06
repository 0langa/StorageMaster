using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scanner;

public static class ScanScopeResolver
{
    public static IReadOnlyList<string> BuildExcludedPaths(AppSettings settings, bool deepScan)
    {
        if (deepScan)
            return [];

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ScanOptions.DefaultExcludedPaths)
            excluded.Add(path);

        if (settings.SkipSystemFolders)
        {
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var systemX86Dir = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);

            if (!string.IsNullOrWhiteSpace(windowsDir))
                excluded.Add(windowsDir);
            if (!string.IsNullOrWhiteSpace(systemDir))
                excluded.Add(systemDir);
            if (!string.IsNullOrWhiteSpace(systemX86Dir))
                excluded.Add(systemX86Dir);
        }

        foreach (var path in settings.ExcludedPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            excluded.Add(path);

        return ScanOptionValidator.NormalizeExcludedPaths(excluded);
    }
}
