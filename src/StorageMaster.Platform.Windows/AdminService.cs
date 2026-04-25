using System.Diagnostics;
using System.Security.Principal;
using StorageMaster.Core.Interfaces;

namespace StorageMaster.Platform.Windows;

public sealed class AdminService : IAdminService
{
    public bool IsRunningAsAdmin { get; } = CheckAdmin();

    private static bool CheckAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void RestartAsAdmin(bool enableDeepScan = false)
    {
        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine process path.");

        var args = enableDeepScan ? "--deep-scan" : string.Empty;

        Process.Start(new ProcessStartInfo
        {
            FileName        = exePath,
            Arguments       = args,
            UseShellExecute = true,
            Verb            = "runas",
        });

        // Terminate this instance after handing off to the elevated process.
        Environment.Exit(0);
    }
}
