using Microsoft.Win32;
using StorageMaster.Core.Interfaces;

namespace StorageMaster.Platform.Windows;

/// <summary>
/// Reads installed program information from the Windows Registry
/// (both HKLM and HKCU uninstall keys, both 32-bit and 64-bit views).
/// </summary>
public sealed class InstalledProgramProvider : IInstalledProgramProvider
{
    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public IReadOnlyList<InstalledProgramInfo> GetInstalledPrograms()
    {
        var results = new List<InstalledProgramInfo>();

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var keyPath in UninstallKeys)
        {
            try
            {
                using var root = hive.OpenSubKey(keyPath);
                if (root is null) continue;

                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subKeyName);
                        if (sub is null) continue;

                        var name = sub.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        // Skip Windows components and updates — they aren't "programs"
                        // in the traditional sense and their paths are system-critical.
                        var systemComponent = sub.GetValue("SystemComponent");
                        if (systemComponent is 1) continue;

                        var installLocation = sub.GetValue("InstallLocation") as string;
                        var publisher       = sub.GetValue("Publisher") as string;

                        results.Add(new InstalledProgramInfo(
                            name.Trim(),
                            string.IsNullOrWhiteSpace(installLocation) ? null : installLocation.Trim(),
                            string.IsNullOrWhiteSpace(publisher)       ? null : publisher.Trim()));
                    }
                    catch { /* skip unreadable sub-keys */ }
                }
            }
            catch { /* skip inaccessible hive keys */ }
        }

        return results;
    }
}
