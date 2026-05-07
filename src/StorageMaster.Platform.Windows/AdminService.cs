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
        var args = enableDeepScan ? "--cli scan --deep --path \"C:\\\"" : "--cli version";
        _ = TryStartElevated(args);
    }

    public bool TryStartElevated(string arguments)
    {
        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine process path.");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        });

        return process is not null;
    }
}
