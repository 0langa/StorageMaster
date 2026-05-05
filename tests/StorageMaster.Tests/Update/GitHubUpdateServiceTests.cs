using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageMaster.Core.Update;

namespace StorageMaster.Tests.Update;

public sealed class GitHubUpdateServiceTests : IDisposable
{
    private readonly string _downloadPath = Path.Combine(Path.GetTempPath(), "StorageMaster", "Updates", "StorageMaster-9.9.9-win-x64-Setup.exe");

    public GitHubUpdateServiceTests()
    {
        var dir = Path.GetDirectoryName(_downloadPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(_downloadPath))
            File.Delete(_downloadPath);
        if (File.Exists(_downloadPath + ".part"))
            File.Delete(_downloadPath + ".part");
    }

    [Fact]
    public async Task CheckAsync_LatestStableReleaseWithMatchingInstaller_ReturnsUpdateInfo()
    {
        var service = CreateService(_ => Json("""
            {
              "tag_name": "v1.6.0",
              "prerelease": false,
              "draft": false,
              "body": "Bug fixes and improvements",
              "published_at": "2026-05-01T12:00:00Z",
              "assets": [
                {
                  "name": "StorageMaster-1.6.0-win-arm64-Setup.exe",
                  "browser_download_url": "https://example.test/arm64.exe"
                },
                {
                  "name": "StorageMaster-1.6.0-win-x64-Setup.exe",
                  "browser_download_url": "https://example.test/win-x64.exe"
                }
              ]
            }
            """));

        var update = await service.CheckAsync();

        update.Should().NotBeNull();
        update!.Version.Should().Be(new Version(1, 6, 0));
        update.TagName.Should().Be("v1.6.0");
        update.AssetName.Should().Be("StorageMaster-1.6.0-win-x64-Setup.exe");
        update.DownloadUrl.Should().Be("https://example.test/win-x64.exe");
    }

    [Fact]
    public async Task CheckAsync_PrereleaseWithSameCoreAsInstalledVersion_IsNotTreatedAsNewer()
    {
        var service = CreateService(_ => Json("""
            [
              {
                "tag_name": "v1.5.0-rc.1",
                "prerelease": true,
                "draft": false,
                "published_at": "2026-05-01T12:00:00Z",
                "assets": [
                  {
                    "name": "StorageMaster-1.5.0-rc.1-win-x64-Setup.exe",
                    "browser_download_url": "https://example.test/rc1.exe"
                  }
                ]
              }
            ]
            """));

        var update = await service.CheckAsync(includePrerelease: true);

        update.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_IncludePrerelease_SelectsNewestSemverRelease()
    {
        var service = CreateService(_ => Json("""
            [
              {
                "tag_name": "v1.6.0-beta.2",
                "prerelease": true,
                "draft": false,
                "published_at": "2026-05-01T12:00:00Z",
                "assets": [
                  {
                    "name": "StorageMaster-1.6.0-beta.2-win-x64-Setup.exe",
                    "browser_download_url": "https://example.test/beta2.exe"
                  }
                ]
              },
              {
                "tag_name": "v1.6.0-beta.10",
                "prerelease": true,
                "draft": false,
                "published_at": "2026-05-02T12:00:00Z",
                "assets": [
                  {
                    "name": "StorageMaster-1.6.0-beta.10-win-x64-Setup.exe",
                    "browser_download_url": "https://example.test/beta10.exe"
                  }
                ]
              }
            ]
            """));

        var update = await service.CheckAsync(includePrerelease: true);

        update.Should().NotBeNull();
        update!.TagName.Should().Be("v1.6.0-beta.10");
        update.AssetName.Should().Be("StorageMaster-1.6.0-beta.10-win-x64-Setup.exe");
    }

    [Fact]
    public async Task CheckAsync_NoMatchingInstallerAsset_ReturnsNull()
    {
        var service = CreateService(_ => Json("""
            {
              "tag_name": "v1.6.0",
              "prerelease": false,
              "draft": false,
              "published_at": "2026-05-01T12:00:00Z",
              "assets": [
                {
                  "name": "StorageMaster-1.6.0-portable.zip",
                  "browser_download_url": "https://example.test/portable.zip"
                }
              ]
            }
            """));

        var update = await service.CheckAsync();

        update.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_InsecureInstallerUrl_ReturnsNull()
    {
        var service = CreateService(_ => Json("""
            {
              "tag_name": "v1.6.0",
              "prerelease": false,
              "draft": false,
              "published_at": "2026-05-01T12:00:00Z",
              "assets": [
                {
                  "name": "StorageMaster-1.6.0-win-x64-Setup.exe",
                  "browser_download_url": "http://example.test/win-x64.exe"
                }
              ]
            }
            """));

        var update = await service.CheckAsync();

        update.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_MalformedJson_ReturnsNull()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        });

        var update = await service.CheckAsync();

        update.Should().BeNull();
    }

    [Fact]
    public async Task DownloadAsync_DisposesPartialFileBeforeRename()
    {
        var payload = Encoding.UTF8.GetBytes("installer-payload");
        var service = CreateService(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/repos/"))
            {
                return Json("""
                    {
                      "tag_name": "v9.9.9",
                      "prerelease": false,
                      "draft": false,
                      "published_at": "2026-05-01T12:00:00Z",
                      "assets": [
                        {
                          "name": "StorageMaster-9.9.9-win-x64-Setup.exe",
                          "browser_download_url": "https://example.test/download.exe"
                        }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
        }, new Version(9, 9, 8));

        var update = await service.CheckAsync();
        var path = await service.DownloadAsync(update!);

        path.Should().Be(_downloadPath);
        File.Exists(path).Should().BeTrue();
        File.ReadAllBytes(path).Should().Equal(payload);
        File.Exists(path + ".part").Should().BeFalse();
    }

    private static GitHubUpdateService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        Version? currentVersion = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var client = new HttpClient(handler, disposeHandler: true);

        return new GitHubUpdateService(
            client,
            currentVersion ?? new Version(1, 5, 0),
            NullLogger<GitHubUpdateService>.Instance);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_downloadPath))
            File.Delete(_downloadPath);
        if (File.Exists(_downloadPath + ".part"))
            File.Delete(_downloadPath + ".part");
    }
}
