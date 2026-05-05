using System.Diagnostics;
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
/// downloads the installer asset, and launches it elevated for installation.
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
    private const string ApiBase         = "https://api.github.com";
    private const string OwnerRepo       = "0langa/StorageMaster";
    private const string AssetNameFormat = "StorageMaster-{0}-win-x64-Setup.exe";

    private readonly HttpClient                    _http;
    private readonly Version                       _currentVersion;
    private readonly ILogger<GitHubUpdateService>  _logger;

    /// <inheritdoc/>
    public UpdateInfo? LastCheckResult { get; private set; }

    public GitHubUpdateService(
        HttpClient                   http,
        Version                      currentVersion,
        ILogger<GitHubUpdateService> logger)
    {
        _http           = http;
        _currentVersion = currentVersion;
        _logger         = logger;
    }

    // ── IUpdateService ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<UpdateInfo?> CheckAsync(
        bool              includePrerelease = false,
        CancellationToken ct               = default)
    {
        LastCheckResult = null;

        var currentVersion = SemanticVersion.FromVersion(_currentVersion);

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

        UpdateInfo? bestUpdate    = null;
        var         bestSemver    = default(SemanticVersion?);
        foreach (var release in releases)
        {
            if (release.Draft)
                continue;

            if (!includePrerelease && release.Prerelease)
                continue;

            if (!TryCreateUpdateInfo(release, out var updateInfo, out var releaseVersion))
                continue;

            if (releaseVersion.CompareTo(currentVersion) <= 0)
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
        UpdateInfo         info,
        IProgress<double>? progress = null,
        CancellationToken  ct       = default)
    {
        if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Updater requires an HTTPS download URL.");
        }

        var downloadDir = Path.Combine(
            Path.GetTempPath(), "StorageMaster", "Updates");
        Directory.CreateDirectory(downloadDir);

        var destPath = Path.Combine(downloadDir, info.AssetName);
        var tempPath = destPath + ".part";

        if (File.Exists(tempPath))
            File.Delete(tempPath);

        try
        {
            using var response = await _http.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null &&
                !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Updater requires HTTPS redirects only.");
            }

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            await using var src  = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dest = new FileStream(
                tempPath,
                FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            var buffer     = new byte[81920];
            long bytesRead = 0;
            int  read;

            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytesRead += read;

                if (progress is not null && totalBytes > 0)
                    progress.Report((double)bytesRead / totalBytes * 100.0);
            }

            await dest.FlushAsync(ct).ConfigureAwait(false);

            if (File.Exists(destPath))
                File.Delete(destPath);

            File.Move(tempPath, destPath);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }

        // Basic validation.
        var fi = new FileInfo(destPath);
        if (!fi.Exists || fi.Length == 0)
            throw new InvalidOperationException(
                $"Downloaded file is empty or missing: {destPath}");

        _logger.LogInformation("Downloaded {Asset} ({Bytes:N0} bytes) to {Path}",
            info.AssetName, fi.Length, destPath);
        return destPath;
    }

    /// <inheritdoc/>
    public bool LaunchInstaller(string installerPath)
    {
        try
        {
            var psi = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Verb            = "runas",   // elevation prompt (UAC)
            };
            var proc = Process.Start(psi);
            if (proc is null)
            {
                _logger.LogError("Process.Start returned null for {Path}", installerPath);
                return false;
            }
            _logger.LogInformation("Installer launched (PID {Pid})", proc.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch installer {Path}", installerPath);
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        GitHubRelease            release,
        out UpdateInfo?          info,
        out SemanticVersion      releaseVersion)
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
            Version      = releaseVersion.ToVersion(),
            TagName      = release.TagName,
            ReleaseNotes = release.Body ?? string.Empty,
            AssetName    = asset.Name!,
            DownloadUrl  = asset.BrowserDownloadUrl,
            PublishedAt  = release.PublishedAt ?? DateTimeOffset.MinValue,
        };

        return true;
    }

    // ── GitHub API response DTOs ──────────────────────────────────────────────

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")]     string?          TagName,
        [property: JsonPropertyName("prerelease")]   bool             Prerelease,
        [property: JsonPropertyName("draft")]        bool             Draft,
        [property: JsonPropertyName("body")]         string?          Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset?  PublishedAt,
        [property: JsonPropertyName("assets")]       GitHubAsset[]?   Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")]                 string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);
}
