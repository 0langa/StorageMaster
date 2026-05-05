namespace StorageMaster.Core.Models;

/// <summary>
/// Describes an available update discovered on GitHub Releases.
/// </summary>
public sealed record UpdateInfo
{
    /// <summary>Parsed semantic version of the release (e.g. 1.6.0).</summary>
    public required Version Version { get; init; }

    /// <summary>Raw GitHub tag name (e.g. "v1.6.0").</summary>
    public required string TagName { get; init; }

    /// <summary>Markdown release notes body from GitHub.</summary>
    public required string ReleaseNotes { get; init; }

    /// <summary>File name of the installer asset (e.g. "StorageMaster-1.6.0-win-x64-Setup.exe").</summary>
    public required string AssetName { get; init; }

    /// <summary>Direct download URL for the installer asset.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>When the release was published on GitHub.</summary>
    public required DateTimeOffset PublishedAt { get; init; }
}
