using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SPBackup.Server;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class ServerTimeZoneUnitTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-zone-" + Guid.NewGuid().ToString("N"));
    private ServerOptions Options => new() { DataDirectory = root };
    private string FilePath => Path.Combine(root, "panel-time-zone.json");
    public ServerTimeZoneUnitTests() => Directory.CreateDirectory(root);
    [Fact]
    public void FormattingConvertsEachInstantIncludingDstOverlapAndFractionalOffsets()
    {
        var utc = new ServerTimeZone(Options).Snapshot();
        Assert.Equal("UTC", utc.ZoneId); Assert.False(utc.LoadWarning); Assert.Equal("Ещё не получена", utc.Time(null));
        var eastern = ServerTimeZone.Resolve("America/New_York"); var clock = new ServerClock(eastern.Id, eastern.Zone);
        Assert.Equal("01.11.2026 01:30:00 -04:00", clock.Time(DateTimeOffset.Parse("2026-11-01T05:30:00Z")));
        Assert.Equal("01.11.2026 01:30:00 -05:00", clock.Time(DateTimeOffset.Parse("2026-11-01T06:30:00Z")));
        var nepal = ServerTimeZone.Resolve("Asia/Kathmandu");
        Assert.Equal("02.01.2026 05:45:00 +05:45", new ServerClock(nepal.Id, nepal.Zone).Time(DateTimeOffset.Parse("2026-01-02T00:00:00Z")));
    }
    [Fact]
    public async Task PreferenceIsPortablePersistentAndExistingRequestSnapshotsStayConsistent()
    {
        var zones = new ServerTimeZone(Options); var request = zones.Snapshot();
        await zones.SaveAsync("Vladivostok Standard Time");
        var restored = new ServerTimeZone(Options).Snapshot();
        Assert.Equal("Asia/Vladivostok", restored.ZoneId);
        var instant = DateTimeOffset.Parse("2026-01-02T20:45:00Z");
        Assert.Equal("03.01.2026 06:45:00 +10:00", restored.Time(instant));
        Assert.Equal("02.01.2026 20:45:00 +00:00", request.Time(instant));
        Assert.Contains(ServerTimeZone.Options(restored.ZoneId), z => z.Id == restored.ZoneId && z.Label.Contains("Владивосток"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }
    [Fact]
    public async Task InvalidIdsAndCancelledWritesPreserveTheSavedPreference()
    {
        var zones = new ServerTimeZone(Options); await zones.SaveAsync("Europe/Moscow");
        var original = await File.ReadAllBytesAsync(FilePath);
        foreach (var bad in new[] { "", "../UTC", "/etc/passwd", "Bad/UnknownZone", new string('x', 129), "UTC\n" })
            await Assert.ThrowsAsync<ServerRequestException>(() => zones.SaveAsync(bad));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => zones.SaveAsync("Asia/Vladivostok", stop.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(FilePath)); Assert.Equal("Europe/Moscow", zones.Snapshot().ZoneId);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact]
    public async Task CorruptOrOversizedPreferenceWarnsAndCanBeRepairedWithoutBlockingStartup()
    {
        foreach (var bad in new[] { "{", "{\"FormatVersion\":2,\"TimeZoneId\":\"UTC\"}", "{\"FormatVersion\":1,\"TimeZoneId\":\"Bad/UnknownZone\"}", new string('x', 4097) })
        {
            await File.WriteAllTextAsync(FilePath, bad);
            var zones = new ServerTimeZone(Options);
            Assert.Equal("UTC", zones.Snapshot().ZoneId); Assert.True(zones.Snapshot().LoadWarning);
            await zones.SaveAsync("Europe/Moscow");
            Assert.False(zones.Snapshot().LoadWarning); Assert.Equal("Europe/Moscow", new ServerTimeZone(Options).Snapshot().ZoneId);
        }
    }
    public void Dispose() => Directory.Delete(root, true);
}

public sealed class ServerTimeZoneTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private ServerTimeZone Zones => fixture.App.Services.GetRequiredService<ServerTimeZone>();
    private async Task<HttpResponseMessage> Save(string id, HttpClient? client = null)
    {
        var http = client ?? fixture.Administrator;
        using var page = await http.GetAsync("system"); page.EnsureSuccessStatusCode();
        var token = ServerFixture.AntiToken(await page.Content.ReadAsStringAsync());
        return await http.PostAsync("management/time-zone", new FormUrlEncodedContent(new Dictionary<string, string> { ["timeZoneId"] = id, ["__RequestVerificationToken"] = token }));
    }
    private async Task<string> Get(string path)
    { using var result = await fixture.Administrator.GetAsync(path); result.EnsureSuccessStatusCode(); return WebUtility.HtmlDecode(await result.Content.ReadAsStringAsync()); }
    [ServerFact]
    public async Task BrowserSelectionChangesAllPanelDatesAndSurvivesRestartWithoutChangingUtcOrPlans()
    {
        await Zones.SaveAsync("UTC");
        var agent = await fixture.EnrollAsync();
        var instant = DateTimeOffset.Parse("2026-01-02T20:45:00Z");
        var run = new SPBackup.Server.Monitoring.MonitoredRun(Guid.NewGuid(), Guid.NewGuid(), "Копия", instant.AddMinutes(-1), instant, "Succeeded", 0, false, "Full", []);
        await fixture.Store.SaveRunAsync(agent.AgentId, run, default);
        await fixture.Store.SaveMonitoringAsync(agent.AgentId, new(instant, "test", [new(run.JobId, "Копия", "FilesAndFolders", true, "Ежедневно 23:00 · UTC", instant, 1)], null, 0), default);
        var before = await fixture.Store.ConfigurationAsync(agent.AgentId, default);
        using var saved = await Save("Asia/Vladivostok"); Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.EndsWith("/system?timeZone=saved", saved.Headers.Location!.OriginalString);
        var html = await Get($"agents/{agent.AgentId}");
        Assert.Contains("03.01.2026 06:45:00 +10:00", html); Assert.Contains("Asia/Vladivostok", html);
        Assert.Contains("Ежедневно 23:00 · UTC", html);
        var state = (await fixture.Store.MonitoringAsync(agent.AgentId, default))!;
        Assert.Equal(instant, state.Snapshot.SentUtc); Assert.Equal(TimeSpan.Zero, state.Snapshot.SentUtc.Offset);
        Assert.Equal(instant, Assert.Single((await fixture.Store.RunsAsync(agent.AgentId, 0, default)).Runs).FinishedUtc);
        Assert.Equal(before, await fixture.Store.ConfigurationAsync(agent.AgentId, default));
        await fixture.RestartAsync();
        Assert.Equal("Asia/Vladivostok", Zones.Snapshot().ZoneId);
        Assert.Contains("Владивосток", await Get("system"));
        var audit = (await fixture.Store.AuditPageAsync(0)).Entries.First();
        Assert.Contains(Zones.Snapshot().Time(audit.ReceivedUtc), await Get("audit"));
        var plan = (await fixture.Store.FileConfigurationHistoryAsync(agent.AgentId, 0, default)).Entries.First();
        Assert.Contains(Zones.Snapshot().Time(plan.SavedUtc), await Get($"agents/{agent.AgentId}/jobs/history"));
    }
    [ServerFact]
    public async Task InvitationExpiryUsesSelectedZoneAndKeepsTheSameUtcLifetime()
    {
        await Zones.SaveAsync("Asia/Vladivostok");
        var html = await Get("connect");
        using var response = await fixture.Administrator.PostAsync("connect", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = ServerFixture.AntiToken(html), ["_handler"] = "invitation", ["Model.Name"] = "Time zone invitation", ["Model.Minutes"] = "30" }));
        response.EnsureSuccessStatusCode();
        var content = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        var date = System.Text.RegularExpressions.Regex.Match(content, "Код действует до <strong>([^<]+)</strong>").Groups[1].Value;
        var expires = DateTimeOffset.ParseExact(date, "dd.MM.yyyy HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.FromHours(10), expires.Offset);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
    }
    [ServerFact]
    public async Task OnlyAdministratorWithCsrfCanSaveAndInvalidValuesDoNotChangeThePreference()
    {
        await Zones.SaveAsync("Europe/Moscow");
        using var anonymous = fixture.Client();
        using var denied = await anonymous.PostAsync("management/time-zone", new FormUrlEncodedContent(new Dictionary<string, string> { ["timeZoneId"] = "UTC" }));
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode); Assert.Contains("/login", denied.Headers.Location!.OriginalString);
        using var noCsrf = fixture.Client(); await fixture.SignInAsync(noCsrf);
        using var forged = await noCsrf.PostAsync("management/time-zone", new FormUrlEncodedContent(new Dictionary<string, string> { ["timeZoneId"] = "UTC" }));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var invalid = await Save("../UTC");
        Assert.EndsWith("/system?timeZone=invalid", invalid.Headers.Location!.OriginalString);
        Assert.Equal("Europe/Moscow", Zones.Snapshot().ZoneId);
        using var wrongContent = await fixture.Administrator.PostAsync("management/time-zone", ServerFixture.Json(new { timeZoneId = "UTC" }));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContent.StatusCode);
        Assert.Equal("Europe/Moscow", Zones.Snapshot().ZoneId);
    }
    [ServerFact]
    public async Task FailedPreferenceWriteLeavesRunningServerAndInMemoryZoneIntact()
    {
        await Zones.SaveAsync("Europe/Moscow");
        var path = Path.Combine(fixture.Root, "data", "panel-time-zone.json");
        var original = await File.ReadAllBytesAsync(path); File.Delete(path); Directory.CreateDirectory(path);
        try
        {
            using var result = await Save("Asia/Vladivostok");
            Assert.EndsWith("/system?timeZone=failed", result.Headers.Location!.OriginalString);
            Assert.Equal("Europe/Moscow", Zones.Snapshot().ZoneId);
            Assert.True(await fixture.Store.ReadyAsync());
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "panel-time-zone.json.*.tmp"));
        }
        finally { Directory.Delete(path); await File.WriteAllBytesAsync(path, original); }
    }
}
