using StorageMaster.Core.Models;

namespace StorageMaster.Core.Interfaces;

/// <summary>
/// Checks for new releases on GitHub, downloads the installer asset, and
/// launches it with elevation so Windows can apply the update.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// The result of the most recent <see cref="CheckAsync"/> call, or <c>null</c>
    /// if no check has been performed yet or the installed version is current.
    /// Populated by the silent startup check so the Settings page can reflect
    /// update availability without an extra network round-trip.
    /// </summary>
    UpdateInfo? LastCheckResult { get; }

    /// <summary>
    /// Failure category from the most recent updater operation, or <c>null</c>
    /// if the last operation succeeded.
    /// </summary>
    UpdateFailureKind? LastFailureKind { get; }

    /// <summary>
    /// Queries <c>GET /repos/{owner}/{repo}/releases/latest</c> and returns an
    /// <see cref="UpdateInfo"/> when a newer version with a matching installer
    /// asset is found, or <c>null</c> otherwise.
    /// </summary>
    /// <param name="includePrerelease">
    /// When <c>false</c> (default), pre-release releases are ignored.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<UpdateInfo?> CheckAsync(bool includePrerelease = false, CancellationToken ct = default);

    /// <summary>
    /// Downloads the installer asset from <paramref name="info"/> to
    /// <c>%TEMP%\StorageMaster\Updates\</c> and returns the local file path.
    /// </summary>
    /// <param name="progress">Receives values in [0, 100].</param>
    Task<string> DownloadAsync(
        UpdateInfo          info,
        IProgress<double>?  progress = null,
        CancellationToken   ct       = default);

    /// <summary>
    /// Launches the installer at <paramref name="installerPath"/> elevated
    /// (<c>runas</c>) without a silent flag so the user sees the Inno Setup UI.
    /// Returns <c>true</c> if the process started successfully.
    /// The caller is responsible for exiting the application afterwards.
    /// </summary>
    bool LaunchInstaller(string installerPath);
}
