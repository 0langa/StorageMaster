using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

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
        var args = enableDeepScan
            ? CommandLineArguments.Join("--cli", "scan", "--deep", "--path", @"C:\")
            : "--cli version";
        _ = TryStartElevated(args);
    }

    public bool TryStartElevated(string arguments)
    {
        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine process path.");

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });

            return process is not null;
        }
        catch (Win32Exception)
        {
            // ERROR_CANCELLED (1223): the user declined the UAC prompt.
            // Any other launch failure is equally non-fatal for the caller.
            return false;
        }
    }
}
