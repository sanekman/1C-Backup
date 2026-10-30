using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class GoogleDriveAuthorizationTests
{
    private sealed class OAuth : HttpMessageHandler
    {
        public string? Challenge;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
            var values = Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("authorization_code", values["grant_type"]);
            Assert.Equal("fixture-code", values["code"]);
            Assert.StartsWith("http://127.0.0.1:", values["redirect_uri"]);
            Assert.Equal(Challenge, Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(values["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { refresh_token = "fixture-refresh" })) };
        }
    }
    private static Dictionary<string, string> Parse(string value) => value.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
    [Fact]
    public async Task BrowserFlowRejectsWrongStateAndExchangesMatchingCallbackWithPkce()
    {
        using var oauth = new OAuth(); using var http = new HttpClient(oauth);
        Task? browser = null;
        var result = await GoogleDriveAuthorization.ConnectAsync(new("fixture.apps.googleusercontent.com", "fixture-secret"), http, url =>
        {
            var uri = new Uri(url); Assert.Equal("accounts.google.com", uri.Host);
            var query = Parse(uri.Query);
            Assert.Equal("https://www.googleapis.com/auth/drive.file", query["scope"]);
            Assert.Equal("offline", query["access_type"]); Assert.Equal("S256", query["code_challenge_method"]);
            oauth.Challenge = query["code_challenge"];
            browser = Task.Run(async () =>
            {
                using var callback = new HttpClient();
                using var bad = await callback.GetAsync(query["redirect_uri"] + "?state=wrong&code=attacker");
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                using var good = await callback.GetAsync(query["redirect_uri"] + "?state=" + query["state"] + "&code=fixture-code");
                Assert.Equal(HttpStatusCode.OK, good.StatusCode);
            });
        }, CancellationToken.None);
        await browser!; Assert.Equal("fixture-refresh", result.RefreshToken);
    }
    private sealed class Denied(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("fixture-private-token-and-account") });
    }
    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)]
    public async Task ErrorMessagesDoNotExposeGoogleResponseBody(int status)
    {
        using var http = new HttpClient(new Denied((HttpStatusCode)status));
        var drive = new GoogleDriveClient(http, new("fixture.apps.googleusercontent.com", "fixture-secret", "fixture-refresh"));
        var error = await Assert.ThrowsAsync<IOException>(() => drive.CheckFolderAsync("Folder_A", CancellationToken.None));
        Assert.DoesNotContain("fixture-private", error.Message);
        Assert.DoesNotContain("fixture-secret", error.Message);
        Assert.DoesNotContain("fixture-refresh", error.Message);
    }
    [Fact]
    public async Task CancelledBrowserWaitStopsWithoutTokenExchange()
    {
        using var http = new HttpClient(new OAuth()); using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoogleDriveAuthorization.ConnectAsync(new("fixture.apps.googleusercontent.com", "fixture-secret"), http,
            _ => cancellation.Cancel(), cancellation.Token));
    }
}
