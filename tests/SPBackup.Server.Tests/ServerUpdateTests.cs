using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class LinuxUpdateFactAttribute : FactAttribute
{
    public LinuxUpdateFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Unix update control socket"; }
}
public sealed class ServerLinuxUpdateFactAttribute : FactAttribute
{
    public ServerLinuxUpdateFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONECBACKUP_SERVER_TEST_CONNECTION_FILE")))
            Skip = "Linux + isolated PostgreSQL required";
    }
}

internal sealed class UpdateBrokerFixture : IAsyncDisposable
{
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    public readonly ConcurrentQueue<string> Requests = new();
    public string Phase = "available";
    public bool Malformed;
    public string? RawResponse;
    public ServerUpdateResult? LastResult;
    public UpdateBrokerFixture(string path)
    {
        listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(10);
        worker = Serve();
    }
    private async Task Serve()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var connection = await listener.AcceptAsync(stop.Token);
                using var stream = new NetworkStream(connection);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(stop.Token);
                Requests.Enqueue(line!);
                using var request = JsonDocument.Parse(line!);
                var command = request.RootElement.GetProperty("command").GetString();
                if (command == "check") Phase = "checking";
                if (command == "start") Phase = "queued";
                var result = RawResponse ?? (Malformed ? "{bad" : JsonSerializer.Serialize(new { phase = Phase,
                    currentVersion = "0.1.0-preview.9", latestVersion = "0.1.0-preview.10",
                    targetVersion = command == "start" ? "0.1.0-preview.10" : null, checkedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), lastResult = LastResult }));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(result + "\n"), stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Dispose(); await worker; stop.Dispose(); }
}

public sealed class ServerUpdateClientTests
{
    [Theory]
    [InlineData("0.1.0-preview.10", true)]
    [InlineData("0.1.0-preview.0", false)]
    [InlineData("0.1.0-preview.10\n", false)]
    [InlineData("https://other/program", false)]
    [InlineData("0.1.0-preview.10;id", false)]
    public void OnlyCompatibleReleaseIdentifiersAreAccepted(string value, bool valid) => Assert.Equal(valid, ServerUpdates.ValidVersion(value));
    [Fact]
    public void OldChecksAndLostConnectionsDoNotOfferInstallOrReportSuccess()
    {
        Assert.False(new ServerUpdateState("available", LatestVersion: "0.1.0-preview.10", CheckedUtc: 1).CanStart);
        Assert.False(new ServerUpdateState("unavailable").CanStart);
        Assert.Contains("не подтверждён", new ServerUpdateState("unavailable").Message);
        Assert.Contains("восстановлены", new ServerUpdateState("rolledBack").Message);
    }
    [LinuxUpdateFact]
    public async Task ClientUsesFixedJsonProtocolAndCannotSendACommandOrUrl()
    {
        var root=Path.Combine(Path.GetTempPath(),"sp-update-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"control.sock");await using var broker=new UpdateBrokerFixture(path);
            var client=new ServerUpdates(new ServerOptions {UpdateControlSocketPath=path});
            var status=await client.ReadAsync();Assert.True(status.CanStart);Assert.Equal("0.1.0-preview.9",status.CurrentVersion);
            Assert.Equal("checking",(await client.CheckAsync()).Phase);
            Assert.Equal("queued",(await client.StartAsync("0.1.0-preview.10")).Phase);
            var error=await Assert.ThrowsAsync<ServerRequestException>(()=>client.StartAsync("0.1.0-preview.10;sh"));Assert.Equal(400,error.Status);
            Assert.Equal(3,broker.Requests.Count);
            Assert.Equal("{\"command\":\"status\"}",broker.Requests.First());
            Assert.Equal("{\"command\":\"start\",\"version\":\"0.1.0-preview.10\"}",broker.Requests.Last());
            broker.Malformed=true;Assert.Equal("unavailable",(await client.ReadAsync()).Phase);
            broker.Malformed=false;broker.RawResponse="{\"phase\":\"updated\"}";
            Assert.Equal("unavailable",(await client.ReadAsync()).Phase);
            broker.RawResponse=null;broker.Phase="available";
            broker.LastResult=new("updated","fixture-job","0.1.0-preview.9",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"/var/backups/fixture");
            var checkedState=await client.CheckAsync();Assert.Equal("checking",checkedState.Phase);
            Assert.Equal("/var/backups/fixture",checkedState.LastResult!.BackupDirectory);
        }
        finally { Directory.Delete(root,true); }
    }
    [LinuxUpdateFact]
    public async Task AbsentSocketDoesNotClaimThatTheServerIsUpdated()
    {
        var client=new ServerUpdates(new ServerOptions {UpdateControlSocketPath="/tmp/sp-update-missing-"+Guid.NewGuid().ToString("N")});
        Assert.Equal("notConfigured",(await client.ReadAsync()).Phase);
    }
}

public sealed class ServerUpdateAdministrationTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task UpdateRoutesRequireAdministratorAndCsrfAndRejectUntrustedVersions()
    {
        using var anonymous=fixture.Client();
        using var denied=await anonymous.GetAsync("api/v1/management/server-update");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        using var deniedStart=await anonymous.PostAsync("management/server-update/start",new FormUrlEncodedContent(new Dictionary<string,string>{{"version","0.1.0-preview.10"}}));
        Assert.Equal(HttpStatusCode.Redirect,deniedStart.StatusCode);Assert.Contains("/login",deniedStart.Headers.Location!.OriginalString);
        var enrolled=await fixture.EnrollAsync();using var agent=fixture.Client();agent.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",enrolled.Token);
        using var forbidden=await agent.GetAsync("api/v1/management/server-update");Assert.Equal(HttpStatusCode.Unauthorized,forbidden.StatusCode);
        using var noCsrf=fixture.Client();await fixture.SignInAsync(noCsrf);
        using var forged=await noCsrf.PostAsync("management/server-update/check",new FormUrlEncodedContent(new Dictionary<string,string>()));Assert.Equal(HttpStatusCode.BadRequest,forged.StatusCode);
        using var invalid=await fixture.Administrator.PostAsync("management/server-update/start",new FormUrlEncodedContent(new Dictionary<string,string>{{"version","https://other/program"}}));Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        using var status=await fixture.Administrator.GetAsync("api/v1/management/server-update");Assert.True(status.Headers.CacheControl!.NoStore);
    }
    [ServerLinuxUpdateFact]
    public async Task SettingsOfferOnlyCheckedReleaseAndShowRunningTaskAfterRedirectAndRestart()
    {
        await using var broker=new UpdateBrokerFixture(fixture.UpdateSocketPath);
        broker.LastResult=new("updated","old-job","0.1.0-preview.9",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"/var/backups/fixture");
        using var page=await fixture.Administrator.GetAsync("settings");page.EnsureSuccessStatusCode();
        var html=WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());Assert.Contains("Настройки сервера",html);Assert.Contains("Обновить до 0.1.0-preview.10",html);
        using var start=await fixture.Administrator.PostAsync("management/server-update/start",new FormUrlEncodedContent(new Dictionary<string,string>{{"version","0.1.0-preview.10"}}));
        Assert.EndsWith("/settings",start.Headers.Location!.OriginalString);
        await fixture.RestartAsync();
        using var running=await fixture.Administrator.GetAsync("settings");
        var busy=WebUtility.HtmlDecode(await running.Content.ReadAsStringAsync());Assert.Contains("Обновление выполняется",busy);Assert.DoesNotContain(">Обновить до",busy);
        Assert.Contains("data-update-phase=\"queued\"",busy);
        Assert.Contains("Последнее обновление",busy);Assert.Contains("/var/backups/fixture",busy);
        broker.Phase="rolledBack";
        using var restored=await fixture.Administrator.GetAsync("settings");Assert.Contains("Прежняя программа и данные восстановлены",WebUtility.HtmlDecode(await restored.Content.ReadAsStringAsync()));
    }
}
