using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;

namespace StorageMaster.Core.Update;

/// <summary>
/// Checks the GitHub Releases API for a newer version of StorageMaster,
/// downloads the installer asset, and launches its per-user installer.
///
/// Design notes:
///   • Uses a single shared <see cref="HttpClient"/> (caller-owned, injected).
///   • HTTPS enforced via <c>HttpClient.BaseAddress</c> — plain-HTTP redirects
///     are not followed (HttpClient default policy).
///   • No auth token required — the GitHub API allows public release queries
///     without authentication (60 req/hour unauthenticated rate limit is ample).
///   • <see cref="LastCheckResult"/> caches the most recent positive check so the
///     Settings page can show update availability without an extra network call.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    private const string ApiBase = "https://api.github.com";
    private const string OwnerRepo = "0langa/StorageMaster";
    private const string AssetNameFormat = "StorageMaster-{0}-win-x64-Setup.exe";

    private readonly HttpClient _http;
    private readonly SemanticVersion _currentVersion;
    private readonly ILogger<GitHubUpdateService> _logger;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IInstallerTrustVerifier _installerTrustVerifier;
    private readonly Func<ProcessStartInfo, int?> _installerLauncher;
    private readonly ConcurrentDictionary<string, ValidatedInstaller> _validatedInstallers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public UpdateInfo? LastCheckResult { get; private set; }
    public UpdateFailureKind? LastFailureKind { get; private set; }

    public GitHubUpdateService(
        HttpClient http,
        Version currentVersion,
        ILogger<GitHubUpdateService> logger,
        ISettingsRepository settingsRepository,
        IInstallerTrustVerifier installerTrustVerifier)
        : this(
            http,
            currentVersion.ToString(3),
            logger,
            settingsRepository,
            installerTrustVerifier)
    {
    }

    public GitHubUpdateService(
        HttpClient http,
        string currentVersion,
        ILogger<GitHubUpdateService> logger,
        ISettingsRepository settingsRepository,
        IInstallerTrustVerifier installerTrustVerifier)
        : this(
            http,
            currentVersion,
            logger,
            settingsRepository,
            installerTrustVerifier,
            LaunchProcess)
    {
    }

    internal GitHubUpdateService(
        HttpClient http,
        string currentVersion,
        ILogger<GitHubUpdateService> logger,
        ISettingsRepository settingsRepository,
        IInstallerTrustVerifier installerTrustVerifier,
        Func<ProcessStartInfo, int?> installerLauncher)
    {
        _http = http;
        _currentVersion = SemanticVersion.TryParseTag(currentVersion, out var parsed)
            ? parsed
            : default;
        _logger = logger;
        _settingsRepository = settingsRepository;
        _installerTrustVerifier = installerTrustVerifier;
        _installerLauncher = installerLauncher;
    }

    // ── IUpdateService ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<UpdateInfo?> CheckAsync(
        bool includePrerelease = false,
        CancellationToken ct = default)
    {
        LastCheckResult = null;
        LastFailureKind = null;

        IReadOnlyList<GitHubRelease> releases;
        try
        {
            releases = includePrerelease
                ? await GetReleasesAsync(ct).ConfigureAwait(false)
                : await GetLatestReleaseAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("No releases found for {Repo}", OwnerRepo);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "GitHub returned malformed release metadata");
            return null;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogWarning(ex, "GitHub returned an unsupported release payload");
            return null;
        }

        UpdateInfo? bestUpdate = null;
        var bestSemver = default(SemanticVersion?);
        foreach (var release in releases)
        {
            if (release.Draft)
                continue;

            if (!includePrerelease && release.Prerelease)
                continue;

            if (!TryCreateUpdateInfo(release, out var updateInfo, out var releaseVersion))
                continue;

            if (releaseVersion.CompareTo(_currentVersion) <= 0)
                continue;

            if (bestSemver is null || releaseVersion.CompareTo(bestSemver.Value) > 0)
            {
                bestUpdate = updateInfo;
                bestSemver = releaseVersion;
            }
        }

        LastCheckResult = bestUpdate;
        if (bestUpdate is not null)
            _logger.LogInformation("Update available: {Tag} ({Asset})", bestUpdate.TagName, bestUpdate.AssetName);

        return bestUpdate;
    }

    /// <inheritdoc/>
    public async Task<string> DownloadAsync(
        UpdateInfo info,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        LastFailureKind = null;

        if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateFailureKind.InsecureDownloadUrl, "Updater requires an HTTPS download URL.");
        }

        var downloadDir = Path.Combine(
            Path.GetTempPath(), "StorageMaster", "Updates");
        Directory.CreateDirectory(downloadDir);
        CleanupStalePartialFiles(downloadDir);

        var destPath = Path.Combine(downloadDir, info.AssetName);
        var tempPath = destPath + ".part";

        TryDeleteIfExists(tempPath);

        try
        {
            using var response = await _http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateException(
                    UpdateFailureKind.MissingInstallerAsset,
                    $"Installer asset is no longer available: {info.AssetName}.");
            }

            response.EnsureSuccessStatusCode();

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null &&
                !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Updater requires HTTPS redirects only.");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dest = new FileStream(
                tempPath,
                FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long bytesRead = 0;
                int read;

                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    bytesRead += read;

                    if (progress is not null && totalBytes > 0)
                        progress.Report((double)bytesRead / totalBytes * 100.0);
                }

                await dest.FlushAsync(ct).ConfigureAwait(false);
            }

            // Validate while payload still has its private .part name. An
            // untrusted or truncated download must never appear at final,
            // launchable installer path.
            var tempInfo = new FileInfo(tempPath);
            if (!tempInfo.Exists || tempInfo.Length == 0)
            {
                throw new UpdateException(
                    UpdateFailureKind.MissingInstallerAsset,
                    $"Downloaded file is empty or missing: {tempPath}");
            }

            var actualDigest = await ComputeSha256HexAsync(tempPath, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(info.Sha256Digest))
            {
                var expected = NormalizeDigest(info.Sha256Digest);
                if (!string.Equals(expected, actualDigest, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException(
                        UpdateFailureKind.ChecksumMismatch,
                        $"Checksum mismatch for downloaded installer. Expected {expected}, got {actualDigest}.");
                }
            }

            await VerifyInstallerTrustAsync(tempPath, ct).ConfigureAwait(false);

            File.Move(tempPath, destPath, overwrite: true);
            _validatedInstallers[Path.GetFullPath(destPath)] =
                new ValidatedInstaller(actualDigest, tempInfo.Length);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            TryDeleteIfExists(tempPath);
            LastFailureKind = UpdateFailureKind.NetworkTimeout;
            throw new UpdateException(UpdateFailureKind.NetworkTimeout, "Network timeout while downloading update.", ex);
        }
        catch (IOException ex) when (IsFileInUseError(ex))
        {
            TryDeleteIfExists(tempPath);
            LastFailureKind = UpdateFailureKind.DownloadFileInUse;
            throw new UpdateException(
                UpdateFailureKind.DownloadFileInUse,
                "Download failed because the target file is currently in use. Close other installers and try again.",
                ex);
        }
        catch (UpdateException ex)
        {
            TryDeleteIfExists(tempPath);
            LastFailureKind = ex.Kind;
            throw;
        }
        catch
        {
            TryDeleteIfExists(tempPath);
            throw;
        }

        var fi = new FileInfo(destPath);
        PruneOldInstallers(downloadDir, destPath);

        _logger.LogInformation("Downloaded {Asset} ({Bytes:N0} bytes) to {Path}",
            info.AssetName, fi.Length, destPath);
        return destPath;
    }

    /// <inheritdoc/>
    public async Task<bool> LaunchInstallerAsync(
        string installerPath,
        CancellationToken ct = default)
    {
        LastFailureKind = null;
        try
        {
            var fullPath = Path.GetFullPath(installerPath);
            if (!_validatedInstallers.TryGetValue(fullPath, out var expected))
            {
                LastFailureKind = UpdateFailureKind.InvalidSignature;
                _logger.LogError(
                    "Refused installer launch because {Path} was not validated by this updater session",
                    fullPath);
                return false;
            }

            // Deny writers and renames from re-verification through process
            // creation. This closes normal path-swap window between trust
            // validation and elevated launch.
            await using var launchLock = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 131072,
                useAsync: true);

            if (launchLock.Length != expected.Length)
            {
                LastFailureKind = UpdateFailureKind.ChecksumMismatch;
                _logger.LogError("Refused installer launch because {Path} changed after download", fullPath);
                return false;
            }

            var actualDigest = await ComputeSha256HexAsync(launchLock, ct).ConfigureAwait(false);
            if (!string.Equals(actualDigest, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                LastFailureKind = UpdateFailureKind.ChecksumMismatch;
                _logger.LogError("Refused installer launch because {Path} changed after download", fullPath);
                return false;
            }

            await VerifyInstallerTrustAsync(fullPath, ct).ConfigureAwait(false);

            // StorageMaster ships a per-user installer (PrivilegesRequired=lowest,
            // installing under {localappdata}\Programs). Requesting elevation here
            // would prompt for admin rights the install does not need, and — worse —
            // would run the installer as a different user whose {localappdata} is
            // not the one being upgraded. Launch unelevated and let the installer
            // request elevation itself if a future build ever needs it.
            var psi = new ProcessStartInfo(fullPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
            };
            var pid = _installerLauncher(psi);
            if (pid is null)
            {
                _logger.LogError("Installer launch returned no process for {Path}", installerPath);
                return false;
            }
            _logger.LogInformation("Installer launched (PID {Pid})", pid.Value);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LastFailureKind = UpdateFailureKind.UserCancelledElevation;
            _logger.LogInformation("Installer elevation prompt was cancelled by the user.");
            return false;
        }
        catch (UpdateException ex)
        {
            LastFailureKind = ex.Kind;
            _logger.LogError(ex, "Refused installer launch after trust re-verification");
            return false;
        }
        catch (Exception ex)
        {
            LastFailureKind = UpdateFailureKind.Unknown;
            _logger.LogError(ex, "Failed to launch installer {Path}", installerPath);
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Default installer launcher: starts the process and returns its PID,
    /// or <c>null</c> when the shell reused an existing process and returned none.
    /// Tests substitute this through the internal constructor so no real process
    /// is ever created.
    /// </summary>
    private static int? LaunchProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process?.Id;
    }

    /// <summary>
    /// Strips a leading 'v' from the tag and parses into a <see cref="Version"/>.
    /// Accepts "v1.2.3", "1.2.3", "v1.2.3.4" etc.
    /// Returns false for non-parseable tags (pre-release suffixes, etc.).
    /// </summary>
    internal static bool TryParseTag(string tag, out Version version)
    {
        var normalized = SemanticVersion.NormalizeTag(tag);
        var prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0)
            normalized = normalized[..prereleaseIndex];

        return Version.TryParse(normalized, out version!);
    }

    private async Task<IReadOnlyList<GitHubRelease>> GetLatestReleaseAsync(CancellationToken ct)
    {
        var url = $"{ApiBase}/repos/{OwnerRepo}/releases/latest";
        var release = await _http.GetFromJsonAsync<GitHubRelease>(url, ct).ConfigureAwait(false);
        return release is null ? [] : [release];
    }

    private async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(CancellationToken ct)
    {
        var url = $"{ApiBase}/repos/{OwnerRepo}/releases";
        var releases = await _http.GetFromJsonAsync<List<GitHubRelease>>(url, ct).ConfigureAwait(false);
        return releases ?? [];
    }

    private bool TryCreateUpdateInfo(
        GitHubRelease release,
        out UpdateInfo? info,
        out SemanticVersion releaseVersion)
    {
        info = null;
        releaseVersion = default;

        if (string.IsNullOrWhiteSpace(release.TagName))
        {
            _logger.LogWarning("Skipping GitHub release with an empty tag name");
            return false;
        }

        if (!SemanticVersion.TryParseTag(release.TagName, out releaseVersion))
        {
            _logger.LogWarning("Could not parse semantic version from tag '{Tag}'", release.TagName);
            return false;
        }

        var normalizedVersion = releaseVersion.ToString();
        var expectedAsset = string.Format(AssetNameFormat, normalizedVersion);
        var asset = release.Assets?.FirstOrDefault(
            a => string.Equals(a.Name, expectedAsset, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            _logger.LogWarning(
                "Release {Tag} has no matching asset '{Asset}' — assets found: {Assets}",
                release.TagName,
                expectedAsset,
                release.Assets is null ? "none" : string.Join(", ", release.Assets.Select(a => a.Name ?? "<null>")));
            return false;
        }

        if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) ||
            !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var assetUri) ||
            !string.Equals(assetUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Release {Tag} asset {Asset} does not provide an HTTPS download URL",
                release.TagName, asset.Name);
            return false;
        }

        info = new UpdateInfo
        {
            Version = releaseVersion.ToVersion(),
            TagName = release.TagName,
            ReleaseNotes = release.Body ?? string.Empty,
            AssetName = asset.Name!,
            DownloadUrl = asset.BrowserDownloadUrl,
            ReleaseUrl = release.HtmlUrl ?? $"https://github.com/{OwnerRepo}/releases/tag/{release.TagName}",
            Sha256Digest = NormalizeDigest(asset.Digest),
            IsPrerelease = release.Prerelease,
            PublishedAt = release.PublishedAt ?? DateTimeOffset.MinValue,
        };

        return true;
    }

    // ── GitHub API response DTOs ──────────────────────────────────────────────

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("assets")] GitHubAsset[]? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);

    private async Task VerifyInstallerTrustAsync(string installerPath, CancellationToken ct)
    {
        var settings = await _settingsRepository.LoadAsync(ct).ConfigureAwait(false);
        var trust = await _installerTrustVerifier.VerifyAsync(installerPath, ct).ConfigureAwait(false);

        if (trust.IsSigned)
        {
            if (!trust.IsSignatureValid || !trust.HasTrustedTimestamp)
            {
                LastFailureKind = UpdateFailureKind.InvalidSignature;
                throw new UpdateException(
                    UpdateFailureKind.InvalidSignature,
                    $"Installer signature is invalid or missing a trusted timestamp ({trust.Status}).");
            }
            return;
        }

        if (settings.RequireSignedUpdates)
        {
            LastFailureKind = UpdateFailureKind.InvalidSignature;
            throw new UpdateException(
                UpdateFailureKind.InvalidSignature,
                "Installer is unsigned and 'Require signed update installers' is enabled.");
        }

        _logger.LogWarning("Installer {Path} is unsigned. Continuing because RequireSignedUpdates=false.", installerPath);
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 131072,
            useAsync: true);
        return await ComputeSha256HexAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256HexAsync(Stream stream, CancellationToken ct)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;

        var trimmed = digest.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["sha256:".Length..];

        return trimmed.ToLowerInvariant();
    }

    private static void CleanupStalePartialFiles(string downloadDir)
    {
        foreach (var path in Directory.EnumerateFiles(downloadDir, "*.part", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (age > TimeSpan.FromHours(6))
                    File.Delete(path);
            }
            catch
            {
                // best-effort cleanup only
            }
        }
    }

    private static void PruneOldInstallers(string downloadDir, string keepPath)
    {
        var installers = Directory.EnumerateFiles(downloadDir, "StorageMaster-*-win-x64-Setup.exe", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(path, keepPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(static fi => fi.LastWriteTimeUtc)
            .ToList();

        foreach (var stale in installers.Skip(2))
        {
            TryDeleteIfExists(stale.FullName);
        }
    }

    private static bool IsFileInUseError(IOException ex) =>
        ex.HResult == unchecked((int)0x80070020) || // ERROR_SHARING_VIOLATION
        ex.HResult == unchecked((int)0x80070021);   // ERROR_LOCK_VIOLATION

    private static void TryDeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup only
        }
    }

    private sealed record ValidatedInstaller(string Sha256, long Length);
}
