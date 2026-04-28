namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Platform abstraction for querying which programs are currently installed.
/// Used by cleanup rules that detect program leftovers.
/// </summary>
public interface IInstalledProgramProvider
{
    /// <summary>
    /// Returns display names (and optional install locations) of all programs
    /// that appear in the system's Add/Remove Programs list.
    /// </summary>
    IReadOnlyList<InstalledProgramInfo> GetInstalledPrograms();
}

public sealed record InstalledProgramInfo(
    string  DisplayName,
    string? InstallLocation,
    string? Publisher);
