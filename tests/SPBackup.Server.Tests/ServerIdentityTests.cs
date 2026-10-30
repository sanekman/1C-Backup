using System.Net;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class ServerIdentityTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [Fact]
    public void FriendlyNamesAreIndependentFromDnsAndLegacyConfigurationRemainsValid()
    {
        Assert.True(new ServerOptions().HasValidIdentity());
        Assert.True(new ServerOptions { DisplayName = "Бухгалтерия — основной сервер №2", PublicAddress = "https://backup.example:7443/" }.HasValidIdentity());
        Assert.True(new ServerOptions { DisplayName = "Филиал", PublicAddress = "https://127.0.0.1:7447/monitoring/" }.HasValidIdentity());
        Assert.True(new ServerOptions { DisplayName = "IPv6", PublicAddress = "https://[::1]:7443/" }.HasValidIdentity());
        Assert.False(new ServerOptions { DisplayName = " " }.HasValidIdentity());
        Assert.False(new ServerOptions { DisplayName = "Name\nInjected" }.HasValidIdentity());
        Assert.False(new ServerOptions { DisplayName = new string('a', 129) }.HasValidIdentity());
    }

    [Fact]
    public void AdvertisedAddressRejectsNonHttpsCredentialsAndAmbiguousAddresses()
    {
        foreach (var address in new[] { "", "backup.example", "http://backup.example", "https://user:password@backup.example/",
            "https://backup.example/?token=x", "https://backup.example/#fragment", "https://backup.example:65536/",
            "https://0.0.0.0:7443/", "https://[::]:7443/", "https://backup.example/a b", "https://backup.example\\path" })
            Assert.False(ServerOptions.ValidPublicAddress(address), address);
    }

    [ServerFact]
    public async Task PanelShowsFriendlyNameAndInvitationUsesConfiguredAddressInsteadOfBrowserHost()
    {
        const string name = "Бухгалтерия <script>untrusted()</script>";
        const string address = "https://backup.example:9443/monitoring/";
        await using var app = fixture.Build(prefix: "/monitoring", displayName: name, publicAddress: address);
        await app.StartAsync();
        using var client = new HttpClient(fixture.Handler()) { BaseAddress = new Uri(app.Urls.Single() + "/monitoring/") };
        using var login = await client.GetAsync("login");
        var loginHtml = await login.Content.ReadAsStringAsync();
        Assert.Contains(name, WebUtility.HtmlDecode(loginHtml)); Assert.DoesNotContain(name, loginHtml);
        await fixture.SignInAsync(client);
        using var connect = await client.GetAsync("connect");
        var anti = ServerFixture.AntiToken(await connect.Content.ReadAsStringAsync());
        using var response = await client.PostAsync("connect", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = anti, ["_handler"] = "invitation", ["Model.Name"] = "Identity test", ["Model.Minutes"] = "15" }));
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("value=\"" + address + "\"", html);
        Assert.Contains(name, WebUtility.HtmlDecode(html)); Assert.DoesNotContain(name, html);
        using var system = await client.GetAsync("system");
        Assert.Contains(address, await system.Content.ReadAsStringAsync());
        await app.StopAsync();
    }
}
