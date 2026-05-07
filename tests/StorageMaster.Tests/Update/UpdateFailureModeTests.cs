using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StorageMaster.Core.Interfaces;
using StorageMaster.Core.Models;
using StorageMaster.Core.Update;
using System.Net;
using System.Text;
using System.Text.Json;

namespace StorageMaster.Tests.Update;

/// <summary>
/// Tests for GitHubUpdateService failure modes: network errors, missing assets,
/// bad HTTP responses, and user cancellation. Uses a fake HttpMessageHandler
/// so no real network calls are made.
/// </summary>
public sealed class UpdateFailureModeTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static GitHubUpdateService BuildService(
        HttpMessageHandler handler,
        string currentVersion = "2.0.0")
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com"),
            DefaultRequestHeaders = { { "User-Agent", "StorageMaster-Tests/1.0" } },
        };

        var settings = new Mock<ISettingsRepository>();
        settings.Setup(r => r.LoadAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AppSettings());

        var trust = new Mock<IInstallerTrustVerifier>();
        trust.Setup(t => t.VerifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new InstallerTrustVerificationResult
             {
                 IsSigned = true,
                 IsSignatureValid = true,
                 HasTrustedTimestamp = true,
             });

        return new GitHubUpdateService(
            http,
            currentVersion,
            NullLogger<GitHubUpdateService>.Instance,
            settings.Object,
            trust.Object);
    }

    private static UpdateInfo MakeUpdateInfo(
        string version = "3.0.0",
        string downloadUrl = "https://github.com/0langa/StorageMaster/releases/download/v3.0.0/StorageMaster-3.0.0-win-x64-Setup.exe") =>
        new()
        {
            Version = Version.Parse(version),
            TagName = $"v{version}",
            ReleaseNotes = "Notes",
            AssetName = $"StorageMaster-{version}-win-x64-Setup.exe",
            DownloadUrl = downloadUrl,
            ReleaseUrl = $"https://github.com/0langa/StorageMaster/releases/tag/v{version}",
            PublishedAt = DateTimeOffset.UtcNow,
        };

    // ── CheckAsync failure modes ───────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_404Response_ReturnsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, "{}");
        var svc = BuildService(handler);

        var result = await svc.CheckAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_MalformedJson_ReturnsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "not-valid-json{{{{");
        var svc = BuildService(handler);

        var result = await svc.CheckAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_EmptyReleaseList_ReturnsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(Array.Empty<object>()));
        // Use includePrerelease=true so the "all releases" endpoint is called.
        var svc = BuildService(handler);

        var result = await svc.CheckAsync(includePrerelease: true);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_CancelledToken_ThrowsOperationCancelled()
    {
        var handler = new HangingHandler();
        var svc = BuildService(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.CheckAsync(ct: cts.Token));
    }

    [Fact]
    public async Task CheckAsync_VersionNotNewer_ReturnsNull()
    {
        // Release has same version as current — not an upgrade.
        var releaseJson = MakeReleasesJson("2.0.0");
        var handler = new FakeHandler(HttpStatusCode.OK, releaseJson);
        var svc = BuildService(handler, currentVersion: "2.0.0");

        var result = await svc.CheckAsync(includePrerelease: true);

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_OlderVersion_ReturnsNull()
    {
        var releaseJson = MakeReleasesJson("1.0.0");
        var handler = new FakeHandler(HttpStatusCode.OK, releaseJson);
        var svc = BuildService(handler, currentVersion: "2.0.0");

        var result = await svc.CheckAsync(includePrerelease: true);

        result.Should().BeNull();
    }

    // ── DownloadAsync failure modes ────────────────────────────────────────────

    [Fact]
    public async Task DownloadAsync_InsecureUrl_ThrowsUpdateException()
    {
        var svc = BuildService(new FakeHandler(HttpStatusCode.OK, "data"));
        var info = MakeUpdateInfo(downloadUrl: "http://insecure.example.com/installer.exe");

        var act = () => svc.DownloadAsync(info);

        await act.Should().ThrowAsync<UpdateException>()
            .Where(e => e.Kind == UpdateFailureKind.InsecureDownloadUrl);
    }

    [Fact]
    public async Task DownloadAsync_404Response_ThrowsMissingAsset()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, string.Empty);
        var svc = BuildService(handler);
        var info = MakeUpdateInfo();

        var act = () => svc.DownloadAsync(info);

        await act.Should().ThrowAsync<UpdateException>()
            .Where(e => e.Kind == UpdateFailureKind.MissingInstallerAsset);
    }

    [Fact]
    public async Task DownloadAsync_NetworkTimeout_ThrowsNetworkTimeout()
    {
        // TaskCanceledException with IsCancellationRequested == false → timeout.
        var handler = new TimeoutHandler();
        var svc = BuildService(handler);
        var info = MakeUpdateInfo();

        var act = () => svc.DownloadAsync(info);

        await act.Should().ThrowAsync<UpdateException>()
            .Where(e => e.Kind == UpdateFailureKind.NetworkTimeout);
    }

    [Fact]
    public async Task DownloadAsync_NetworkTimeout_SetsLastFailureKind()
    {
        var handler = new TimeoutHandler();
        var svc = BuildService(handler);
        var info = MakeUpdateInfo();

        try { await svc.DownloadAsync(info); } catch { }

        svc.LastFailureKind.Should().Be(UpdateFailureKind.NetworkTimeout);
    }

    [Fact]
    public async Task DownloadAsync_UserCancellation_DoesNotSetNetworkTimeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(HttpStatusCode.OK, new string('x', 1024));
        var svc = BuildService(handler);
        var info = MakeUpdateInfo();

        // Cancel immediately before the call completes.
        cts.Cancel();

        try { await svc.DownloadAsync(info, ct: cts.Token); } catch { }

        // User cancellation should NOT classify as NetworkTimeout.
        svc.LastFailureKind.Should().NotBe(UpdateFailureKind.NetworkTimeout);
    }

    // ── LastCheckResult state ─────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_AfterNetworkError_LastCheckResultIsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.ServiceUnavailable, "error");
        var svc = BuildService(handler);

        try { await svc.CheckAsync(); } catch { }

        svc.LastCheckResult.Should().BeNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a minimal GitHub releases JSON array with one release.</summary>
    private static string MakeReleasesJson(string version)
    {
        var assetName = $"StorageMaster-{version}-win-x64-Setup.exe";
        var obj = new
        {
            tag_name = $"v{version}",
            name = $"v{version}",
            body = "Release notes",
            draft = false,
            prerelease = false,
            published_at = DateTimeOffset.UtcNow,
            html_url = $"https://github.com/0langa/StorageMaster/releases/tag/v{version}",
            assets = new[]
            {
                new
                {
                    name = assetName,
                    browser_download_url = $"https://github.com/0langa/StorageMaster/releases/download/v{version}/{assetName}",
                    size = 5_000_000,
                    digest = (string?)null,
                },
            },
        };
        // The "latest" endpoint returns a single object; wrap in an array for the /releases endpoint.
        return JsonSerializer.Serialize(new[] { obj });
    }

    // ── fake HTTP handlers ────────────────────────────────────────────────────

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Simulates an HttpClient internal timeout: throws TaskCanceledException
    /// but with a non-cancelled CancellationToken (the external token is not cancelled).
    /// </summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Throw TaskCanceledException without the external CT being set,
            // which is what HttpClient does on its own internal timeout.
            var ex = new TaskCanceledException("Simulated network timeout.");
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}
