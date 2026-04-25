namespace StorageMaster.Core.Interfaces;

public interface IAdminService
{
    /// <summary>Returns true if the current process is running with administrator privileges.</summary>
    bool IsRunningAsAdmin { get; }

    /// <summary>
    /// Restarts the application with administrator privileges via UAC elevation prompt.
    /// If <paramref name="enableDeepScan"/> is true, passes --deep-scan so the new
    /// process auto-enables deep scan mode on launch.
    /// Exits the current process after spawning the elevated one.
    /// </summary>
    void RestartAsAdmin(bool enableDeepScan = false);
}
