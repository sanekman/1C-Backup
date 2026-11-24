using System.Diagnostics;
using System.Net;
using SPBackup.Server;
using SPBackup.Server.Monitoring;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class NotificationPreviewTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerFact]
    public async Task PreviewFiltersBeforePagingAndUsesFreshScopedEvidence()
    {
        var admin = (await fixture.Store.FindUserAsync("administrator", default))!.Id;
        var root = await fixture.Store.CreateFolderAsync(admin, new("Preview branch"));
        var child = await fixture.Store.CreateFolderAsync(admin, new("Preview leaf", root));
        var outside = await fixture.Store.CreateFolderAsync(admin, new("Outside branch"));
        async Task<EnrollmentResponse> Enroll(string name, Guid folder)
        {
            var invitation = await fixture.Store.InviteAsync(admin, new("Fixture", DisplayName: name, FolderId: folder), default);
            return await fixture.Store.EnrollAsync(new(invitation.Code, Guid.NewGuid(), "HOST"), default);
        }

        var fresh = await Enroll("Current agent", child);
        var offline = await Enroll("Disconnected agent", child);
        var heartbeat = await Enroll("Heartbeat only agent", child);
        var revoked = await Enroll("Revoked agent", child);
        var other = await Enroll("Outside agent", outside);
        var now = DateTimeOffset.UtcNow;
        var job = new MonitoredJob(Guid.NewGuid(), "Test copy", "FilesAndFolders", true, "Daily", null, 1)
            { MaximumBackupAgeHours = 24, LocalAccessReady = false };
        var observation = new MonitoredSource(Guid.NewGuid(), "External SQL", "SqlServerHistory", true, now, false)
            { MaximumBackupAgeHours = 24, FreshnessWarning = true };
        var freshJobs = new[] { job }.Concat(Enumerable.Range(0, 51).Select(index =>
            job with { JobId = Guid.NewGuid(), Name = $"Other copy {index}", LocalAccessReady = true })).ToArray();
        await fixture.Store.SaveMonitoringAsync(fresh.AgentId, new(now, "preview", freshJobs, null, 0, [observation])
            { ExecutionMode = "Managed", ConfigurationRevision = 1, BackupExecutionAvailable = true }, default);
        foreach (var agent in new[] { offline, heartbeat, revoked, other })
            await fixture.Store.SaveMonitoringAsync(agent.AgentId, new(now, "preview", [job], null, 0), default);
        await fixture.ExecuteAsync("""
            INSERT INTO server_configuration_offers(offer_id,agent_id,generation,revision,document_hash,status,reason,observed_utc,received_utc)
            SELECT @offer,a.id,a.token_hash,c.revision,sha256(convert_to(c.document::text,'UTF8')),'Rejected','CacheWriteFailed',now(),now()
            FROM server_agents a JOIN server_configurations c ON c.agent_id=a.id WHERE a.id=@fresh;
            UPDATE server_monitoring SET received_utc=now()-interval '1 hour' WHERE agent_id IN (@offline,@heartbeat);
            UPDATE server_agents SET received_utc=now()-interval '1 hour' WHERE id=@offline;
            """, ("offer", Guid.NewGuid()), ("fresh", fresh.AgentId), ("offline", offline.AgentId), ("heartbeat", heartbeat.AgentId));
        await fixture.Store.HeartbeatAsync(heartbeat.AgentId, new(heartbeat.AgentId, "preview", true, null, now), default);
        await fixture.Store.RevokeAsync(admin, revoked.AgentId, default);

        var page = await fixture.Store.PreviewNotificationProblemsAsync(0, null, null, root, false, 120, default, null);
        Assert.Equal(56, page.Total);
        Assert.Equal(2, page.AffectedAgents);
        Assert.Equal(50, page.Items.Count);
        Assert.Equal("Offline", Assert.Single(page.Items, row => row.AgentId == offline.AgentId).Code);
        Assert.All(page.Items, row => Assert.Contains(row.AgentId, new[] { fresh.AgentId, offline.AgentId }));
        var next = await fixture.Store.PreviewNotificationProblemsAsync(1, null, null, root, false, 120, default, null);
        Assert.Equal(6, next.Items.Count);
        var overdue = await fixture.Store.PreviewNotificationProblemsAsync(0, "Overdue", "External SQL", root, false, 120, default, fresh.AgentId);
        Assert.Equal(AgentProblemScope.Observation, Assert.Single(overdue.Items).Scope);
        Assert.Equal("PlanRejected", Assert.Single((await fixture.Store.PreviewNotificationProblemsAsync(
            0, "PlanRejected", null, root, false, 120, default, fresh.AgentId)).Items).Code);
        Assert.Equal(0, (await fixture.Store.PreviewNotificationProblemsAsync(
            0, null, null, root, false, 120, default, other.AgentId)).Total);

        var url = $"notifications-preview?folder={root}&kind=AccessRequired";
        var html = WebUtility.HtmlDecode(await fixture.Administrator.GetStringAsync(url));
        Assert.Contains("Совпадений: 1", html);
        Assert.Contains("Current agent", html);
        Assert.DoesNotContain("Heartbeat only agent", html);
        Assert.DoesNotContain("Disconnected agent", html);
        Assert.DoesNotContain("Revoked agent", html);
        Assert.DoesNotContain("Outside agent", html);
        Assert.Contains("Предпросмотр правил оповещений", WebUtility.HtmlDecode(await fixture.Administrator.GetStringAsync("settings")));
        using var invalid = await fixture.Administrator.GetAsync("notifications-preview?kind=Failed");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var anonymous = fixture.Client();
        using var denied = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/login", denied.Headers.Location!.OriginalString);
        var operatorPassword = Guid.NewGuid().ToString("N");
        using var created = await fixture.Administrator.PostAsync("management/operators", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = "preview-operator", ["password"] = operatorPassword, ["enabled"] = "true",
            ["grant"] = $"{fresh.AgentId:D}:{job.JobId:D}"
        }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        using var operatorClient = fixture.Client();
        await fixture.SignInAsync(operatorClient, name: "preview-operator", password: operatorPassword);
        using var operatorDenied = await operatorClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, operatorDenied.StatusCode);
        if (Environment.GetEnvironmentVariable("ONECBACKUP_NOTIFICATION_PREVIEW_BROWSER") == "1")
            await VerifyBrowserAsync(root, fresh.AgentId);
    }

    private async Task VerifyBrowserAsync(Guid folder, Guid agent)
    {
        var repository = Environment.GetEnvironmentVariable("ONECBACKUP_TEST_REPOSITORY")
            ?? throw new InvalidOperationException("Set ONECBACKUP_TEST_REPOSITORY for the browser check.");
        var credentials = Path.Combine(fixture.Root, "preview-browser-password.txt");
        await File.WriteAllTextAsync(credentials, fixture.Password);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(credentials, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardError = true, WorkingDirectory = repository };
        foreach (var argument in new[] { "tests/server/notification_preview_browser.py", fixture.Address.ToString(),
            credentials, folder.ToString(), agent.ToString() })
            start.ArgumentList.Add(argument);
        using var browser = Process.Start(start)!;
        var error = browser.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await browser.WaitForExitAsync(timeout.Token); }
        finally { if (!browser.HasExited) browser.Kill(entireProcessTree: true); }
        Assert.True(browser.ExitCode == 0, await error);
    }
}
