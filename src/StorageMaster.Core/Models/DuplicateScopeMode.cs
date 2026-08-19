namespace StorageMaster.Core.Models;

/// <summary>
/// How a duplicate search narrows the scan session it runs over.
/// </summary>
public enum DuplicateScopeMode
{
    /// <summary>Every file the completed scan recorded.</summary>
    WholeSession,

    /// <summary>Only files under the listed folders.</summary>
    IncludedFolders,

    /// <summary>Every file except those under the listed folders.</summary>
    ExcludedFolders,
}
