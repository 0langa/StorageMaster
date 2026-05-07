namespace StorageMaster.Core.Interfaces;

public interface IAdminService
{
    /// <summary>Returns true if the current process is running with administrator privileges.</summary>
    bool IsRunningAsAdmin { get; }

    /// <summary>
    /// Starts the current executable with administrator privileges and the supplied
    /// command-line arguments. The existing UI process remains unelevated.
    /// </summary>
    bool TryStartElevated(string arguments);

    /// <summary>
    /// Compatibility shim for older callers. Starts the CLI deep-scan path instead
    /// of relaunching the full WinUI shell as administrator.
    /// </summary>
    void RestartAsAdmin(bool enableDeepScan = false);
}
