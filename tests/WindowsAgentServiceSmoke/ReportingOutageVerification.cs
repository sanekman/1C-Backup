using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SPBackup.Core;
using SPBackup.Core.Monitoring;

internal static partial class Program
{
    private sealed record ReportingFixture(string Address, Guid AgentId, string Token, string Pin, string FtpPassword, int FtpPort);

    private static async Task<int> VerifyReportingOutageAsync(string agent, string lab)
    {
        var directory = DefaultOwnerProfile;
        agent = Path.GetFullPath(agent);
        Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && File.Exists(agent)
            && WindowsAgentService.QueryInfo() is null && !Directory.Exists(directory),
            "Reporting outage requires a clean disposable Windows runner and an installed agent.");
        var fixture = JsonSerializer.Deserialize<ReportingFixture>(await File.ReadAllTextAsync(Path.Combine(lab, "agent-settings.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing reporting fixture.");
        var credentials = new WindowsFtpCredentialStore();
        var ftpKey = "outage-ftp-" + Guid.NewGuid().ToString("N");
        var serverKey = "outage-server-" + Guid.NewGuid().ToString("N");
        var owner = WindowsIdentity.GetCurrent().User!;
        var account = "spbr" + Guid.NewGuid().ToString("N")[..10];
        var password = "Ro!8a-" + Guid.NewGuid().ToString("N");
        var userCreated = false;
        var profileCreated = false;
        try
        {
            Native.AddUser(account, password);
            userCreated = true;
            var selected = WindowsServiceAccounts.Resolve(account);
            Directory.CreateDirectory(directory);
            profileCreated = true;
            var profileDirectory = new DirectoryInfo(directory);
            var acl = profileDirectory.GetAccessControl();
            acl.SetOwner(owner);
            profileDirectory.SetAccessControl(acl);
            await new WindowsServiceProfile(owner.Value, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                { ServiceSid = selected.Sid }.SaveAsync(directory);
            var source = Path.Combine(directory, "fixture-source");
            Directory.CreateDirectory(source);
            const string contents = "Source bytes for the installed-agent reporting outage";
            await File.WriteAllTextAsync(Path.Combine(source, "example.txt"), contents);
            var job = new BackupJob
            {
                Name = "Reporting outage fixture", Kind = DatabaseKind.FilesAndFolders, Source = source,
                DestinationDirectory = Path.Combine(directory, "local-copies"), ScheduleKind = ScheduleKind.Manual,
                AdditionalDestinations = [new() { StorageKind = StorageKind.Ftp, DestinationDirectory = "/copies",
                    Ftp = new() { Host = "127.0.0.1", Port = fixture.FtpPort, UseTls = false, CredentialKey = ftpKey } }]
            };
            await new JobStore(Path.Combine(directory, "jobs.json"), useLocalTime: true).SaveAsync([job]);
            await ServerMonitoringDispatcher.SaveSettingsAsync(directory, new(fixture.Address, fixture.AgentId, serverKey, fixture.Pin));
            WindowsFtpCredentialStore.SetServiceCache(new(directory, owner.Value, selected.Sid));
            credentials.Save(ftpKey, "fixture", fixture.FtpPassword);
            credentials.Save(serverKey, "agent", fixture.Token);
            await WindowsFtpCredentialStore.ExportProfileForServiceAsync([job], []);
            await credentials.ExportForServiceAsync(serverKey);
            WindowsAgentService.Install(agent, directory, selected.Name, owner.Value, password);
            await WaitService(4);
            var control = new LocalAgentControlStore(directory);
            LocalAgentState? initial = null;
            await Wait(async () => (initial = await control.ReadAsync()) is { Phase: LocalAgentPhase.Running, CanRunManually: true },
                "The installed agent did not become ready.");
            WriteSignal("agent-ready");
            await Wait(() => Task.FromResult(File.Exists(Path.Combine(lab, "start-backup"))), "Backup fixture was not released.");
            await new LocalManualRunQueue(directory).EnqueueAsync(initial!.SessionId, job.Id);
            var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
            await Wait(async () => (await history.ReadAsync()).Any(run => run.JobId == job.Id && run.Status != BackupRunStatus.Running),
                "The backup did not complete while the monitoring server was unavailable.");
            var run = (await history.ReadAsync()).Single();
            Require(run.Status == BackupRunStatus.Succeeded && run.Destinations is { Count: 2 }
                && run.Destinations.All(destination => destination.Record?.Backup is { Length: > 0 }),
                "The backup did not finish both deliveries.");
            var catalog = new BackupCatalog(Path.Combine(directory, "copies.json"));
            var copies = await catalog.ReadAsync();
            Require(copies.Count == 2, "Expected two registered copies.");
            var local = copies.Single(copy => File.Exists(copy.Backup.Location));
            var remote = Directory.GetFiles(Path.Combine(lab, "ftp", "copies")).Single();
            Require(await HashAsync(remote) == await HashAsync(local.Backup.Location), "The delivered copies differ.");
            var restoredTo = Path.Combine(directory, "restore"); Directory.CreateDirectory(restoredTo);
            var restored = await new FileBackupRestorer(new FileBackupStorage(), catalog, Path.Combine(directory, "restore-work"))
                .RestoreAsync(local, job, restoredTo);
            Require(restored.Files == 1 && await File.ReadAllTextAsync(Path.Combine(restored.Directory, "example.txt")) == contents,
                "The file did not restore while the server was unavailable.");
            var result = Path.Combine(lab, "local-result.json");
            await File.WriteAllTextAsync(result + ".tmp", JsonSerializer.Serialize(new
            {
                runId = run.RunId, jobId = job.Id, bytes = new FileInfo(remote).Length
            }));
            File.Move(result + ".tmp", result);
            await Wait(() => Task.FromResult(File.Exists(Path.Combine(lab, "release-agent"))), "Server report was not verified in time.", 120);
            Require((await history.ReadAsync()).Single().RunId == run.RunId &&
                (await control.ReadAsync())?.SessionId == initial.SessionId, "The agent restarted or replayed the backup.");
            WriteSignal("same-session");
        }
        finally
        {
            if (WindowsAgentService.QueryInfo() is { } info && info.Matches(agent, directory))
            {
                if (info.State != 1) { WindowsAgentService.Stop(agent, directory); await WaitService(1); }
                WindowsAgentService.Remove(agent, directory);
                await Wait(() => Task.FromResult(WindowsAgentService.QueryInfo() is null), "Reporting fixture service removal failed.");
            }
            credentials.Delete(ftpKey);
            credentials.Delete(serverKey);
            WindowsFtpCredentialStore.SetServiceCache(null);
            if (profileCreated && WindowsAgentService.QueryInfo() is null) Directory.Delete(directory, true);
            if (userCreated) Require(Native.DeleteUser(account) == 0, "Reporting fixture account cleanup failed.");
        }
        return 0;

        void WriteSignal(string name) => File.WriteAllText(Path.Combine(lab, name), "");
    }
}
