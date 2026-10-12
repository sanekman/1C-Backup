using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace OneCBackup.Server.Tests;

public sealed class ServerPackageFactAttribute : FactAttribute
{
    public ServerPackageFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONECBACKUP_SERVER_TEST_CONNECTION_FILE"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONECBACKUP_SERVER_TEST_PACKAGE_DIR")))
            Skip = "Set the disposable PostgreSQL connection file and published server directory.";
    }
}
public sealed class PackageTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerPackageFact]
    [Trait("Category", "ServerPackage")]
    public async Task StandaloneExecutableMigratesAndServesStaticPanelAndAgentConfiguration()
    {
        var directory = Environment.GetEnvironmentVariable("ONECBACKUP_SERVER_TEST_PACKAGE_DIR")!;
        var executable = Path.Combine(directory, "OneCBackup.Server" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Assert.True(File.Exists(executable));
        var enrolled = await fixture.EnrollAsync();
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)reservation.LocalEndPoint!).Port; reservation.Close();
        var url = "https://127.0.0.1:" + port + "/";
        var configurationFile = Path.Combine(fixture.Root, "package-settings.json");
        File.WriteAllText(configurationFile, System.Text.Json.JsonSerializer.Serialize(new { Urls = url, Server = new {
            ConnectionStringFile = Path.Combine(fixture.Root, "connection.txt"), DataDirectory = Path.Combine(fixture.Root, "package-data"),
            HttpsCertificatePath = Path.Combine(fixture.Root, "certificate.pfx"), HttpsCertificatePasswordFile = Path.Combine(fixture.Root, "certificate-password.txt") } }));
        Process Start(bool migration)
        {
            var info = new ProcessStartInfo(executable) { WorkingDirectory = fixture.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--Server:ConfigFile"); info.ArgumentList.Add(configurationFile);
            if (migration) info.ArgumentList.Add("--migrate");
            return Process.Start(info)!;
        }
        using (var migration = Start(true))
        {
            var stderr = migration.StandardError.ReadToEndAsync(); var stdout = migration.StandardOutput.ReadToEndAsync();
            await migration.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(migration.ExitCode == 0, await stderr); Assert.Contains("обновлена", await stdout);
        }
        using var server = Start(false); var errorOutput = server.StandardError.ReadToEndAsync(); var normalOutput = server.StandardOutput.ReadToEndAsync();
        try
        {
            using var client = new HttpClient(fixture.Handler()) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(3) };
            var ready = false; var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !server.HasExited)
            {
                try { using var health = await client.GetAsync("health/ready"); ready = health.StatusCode == HttpStatusCode.OK; if (ready) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            Assert.True(ready, server.HasExited ? await errorOutput : "Published server did not become ready.");
            using var style = await client.GetAsync("app.css"); Assert.Equal(HttpStatusCode.OK, style.StatusCode);
            Assert.Contains("@media", await style.Content.ReadAsStringAsync());
            await fixture.SignInAsync(client);
            using var panel = await client.GetAsync(""); Assert.Equal(HttpStatusCode.OK, panel.StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrolled.Token);
            using var configuration = await client.GetAsync("api/v1/agents/" + enrolled.AgentId + "/configuration"); Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
            Assert.Contains(enrolled.AgentId.ToString(), await configuration.Content.ReadAsStringAsync());
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(); await errorOutput; await normalOutput;
        }
    }
}
