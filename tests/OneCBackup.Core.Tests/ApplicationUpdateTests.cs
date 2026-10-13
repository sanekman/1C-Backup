using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class ApplicationUpdateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "onec-update-test-" + Guid.NewGuid());
    private static byte[] Installer => "MZfixture installer content"u8.ToArray();
    private static UpdateManifest Manifest(string version = "0.1.0-preview.42") => new(1, version, "win-x64", "nsis",
        "1C-Backup-windows-x64-setup.exe", Installer.Length, Convert.ToHexString(SHA256.HashData(Installer)).ToLowerInvariant());
    private static ApplicationUpdate Update => new(Manifest(), "distribution/windows-x64-preview.42");
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Release(string version, bool draft = false, bool preview = true) => new
    { tag_name = "v" + version, draft, prerelease = preview, body = "<!-- onec-update-manifest: distribution/windows-x64-" + version + " -->" };

    [Theory]
    [InlineData("0.1.0-preview.9", "0.1.0-preview.10", -1)]
    [InlineData("0.1.0-preview.39", "0.1.0-preview.40", -1)]
    [InlineData("0.1.0", "0.1.0-preview.999", 1)]
    [InlineData("0.2.0-alpha.1", "0.1.0", 1)]
    [InlineData("0.2.0-alpha.1", "0.1.0-preview.45", 1)]
    [InlineData("0.1.0-alpha", "0.1.0-preview.1", -1)]
    [InlineData("0.1.0-preview.1+buildA", "0.1.0-preview.1+buildB", 0)]
    public void VersionOrderingPreventsDowngrades(string left, string right, int expected)
        => Assert.Equal(expected, Math.Sign(ApplicationReleaseVersion.Parse(left).CompareTo(ApplicationReleaseVersion.Parse(right))));

    [Fact]
    public async Task SelectsHighestNewPublishedVersionAndUsesOnlyFixedAuthenticatedApi()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.DoesNotContain("ghp_fixture", request.RequestUri.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("ghp_fixture", request.Headers.Authorization.Parameter);
            Assert.Equal(HttpVersion.Version11, request.Version);
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/releases")
                ? Json(new[] { Release("0.1.0-preview.41"), Release("0.1.0-preview.43", draft: true), Release("0.1.0-preview.42") })
                : Json(Manifest()));
        }));
        var found = await new GitHubApplicationUpdates(client).CheckAsync("0.1.0-preview.40", true, "ghp_fixture");
        Assert.Equal("0.1.0-preview.42", found!.Manifest.Version);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("abc.def_ghi-jkl~123+/==")]
    [InlineData("ghp_fixture")]
    public async Task StandardBearerTokenCharactersAreAcceptedWithoutBeingPlacedInUrl(string secret)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(secret, request.Headers.Authorization!.Parameter);
            Assert.DoesNotContain(secret, request.RequestUri!.ToString());
            return Task.FromResult(Json(Array.Empty<object>()));
        }));
        Assert.Null(await new GitHubApplicationUpdates(client).CheckAsync("0.1.0-preview.40", true, secret));
        Assert.False(GitHubApplicationUpdates.IsValidToken("token\r\nInjected: header"));
        Assert.False(GitHubApplicationUpdates.IsValidToken("token with spaces"));
    }

    [Fact]
    public async Task StableChannelExcludesPreviewAndPublishedFlagMismatch()
    {
        using var client = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/releases")
                ? Json(new[] { Release("0.2.0-preview.1"), Release("0.1.1", preview: false), Release("0.3.0-preview.9", preview: false) })
                : Json(Manifest("0.1.1")))));
        Assert.Equal("0.1.1", (await new GitHubApplicationUpdates(client).CheckAsync("0.1.0", false))!.Manifest.Version);
    }

    [Fact]
    public async Task SameOrOlderVersionsDoNotFetchOrInstallManifest()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Json(new[] { Release("0.1.0-preview.40"), Release("0.1.0-preview.39") })); }));
        Assert.Null(await new GitHubApplicationUpdates(client).CheckAsync("0.1.0-preview.40", true));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(302)]
    public async Task AccessErrorsAndRedirectsDoNotExposeSecretOrFetchExternalUrls(int status)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Location = new Uri("https://untrusted.example/installer");
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new GitHubApplicationUpdates(client).CheckAsync("0.1.0", true, "ghp_fixture"));
        Assert.DoesNotContain("ghp_fixture", error.Message); Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ManifestMustMatchSelectedReleaseAndExpectedInstallerFormat()
    {
        var bad = new[] { Manifest() with { Version = "0.1.0-preview.39" }, Manifest() with { Platform = "linux-x64" },
            Manifest() with { InstallerFile = "../../other.exe" }, Manifest() with { Sha256 = "invalid" },
            Manifest() with { SchemaVersion = 2 }, Manifest() with { Size = 101L * 1024 * 1024 }, Manifest() with { InstallerKind = "unsupported" } };
        foreach (var manifest in bad)
        {
            using var client = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/releases")
                ? Json(new[] { Release("0.1.0-preview.42") }) : Json(manifest))));
            await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubApplicationUpdates(client).CheckAsync("0.1.0-preview.40", true));
        }
    }

    [Fact]
    public async Task DownloadVerifiesBytesAndReusesOnlyVerifiedCachedInstaller()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) }); }));
        var service = new GitHubApplicationUpdates(client);
        var path = await service.DownloadAsync(Update, directory);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(path));
        Assert.True(await GitHubApplicationUpdates.IsVerifiedAsync(path, Update.Manifest));
        Assert.Equal(path, await service.DownloadAsync(Update, directory)); Assert.Equal(1, requests);
        await File.WriteAllTextAsync(path, "corrupt cache");
        Assert.Equal(path, await service.DownloadAsync(Update, directory)); Assert.Equal(2, requests);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CorruptOrNonExecutableDownloadsAreNotPublished(bool missingHeader)
    {
        var bytes = Installer;
        bytes[missingHeader ? 0 : bytes.Length - 1] ^= 1;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubApplicationUpdates(client).DownloadAsync(Update, directory));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task TruncatedInstallerIsRejectedBeforePublishing()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer[..^1]) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubApplicationUpdates(client).DownloadAsync(Update, directory));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CancellationLeavesNoExecutableOrPartialDownload()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler((_, token) =>
        { cancellation.Cancel(); return Task.FromCanceled<HttpResponseMessage>(token); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubApplicationUpdates(client).DownloadAsync(Update, directory, token: cancellation.Token));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task PreferencesPersistWithoutTokenValue()
    {
        var path = Path.Combine(directory, "updates.json");
        var store = new ApplicationUpdateSettingsStore(path);
        Assert.False((await store.LoadAsync()).UpdateOnExit);
        var settings = new ApplicationUpdateSettings { AutomaticChecks = true, UpdateOnExit = true, IncludePreview = false, HasGitHubToken = true };
        await store.SaveAsync(settings);
        Assert.Equal(settings, await store.LoadAsync());
        Assert.DoesNotContain("ghp_", await File.ReadAllTextAsync(path));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
