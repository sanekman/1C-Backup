using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using SPBackup.Core;

internal static partial class Program
{
    private static async Task<int> VerifyLiveAccessAsync(string agent, string output)
    {
        var directory = DefaultOwnerProfile;
        agent = Path.GetFullPath(agent);
        Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && File.Exists(agent)
            && WindowsAgentService.QueryInfo() is null && !Directory.Exists(directory),
            "Live access fixture requires a clean disposable Windows runner and an installed agent.");
        Directory.CreateDirectory(output);
        var owner = WindowsIdentity.GetCurrent().User!;
        var users = Enumerable.Range(0, 3).Select(_ => (Name: "spba" + Guid.NewGuid().ToString("N")[..10],
            Password: "Ac!8a-" + Guid.NewGuid().ToString("N"))).ToArray();
        var created = new List<string>();
        var secrets = new WindowsNetworkShareCredentialStore();
        var networkKeys = new List<string>();
        var archive = new WindowsArchivePasswordStore();
        var archiveKeys = new List<string>();
        var tokenKey = "live-access-" + Guid.NewGuid().ToString("N");
        var tokens = new WindowsFtpCredentialStore();
        var profileCreated = false;
        var stage = "setup";
        var checks = new List<string>();
        Guid? firstId = null, secondId = null;
        WindowsSmbFixture? smb = null;
        await using var server = new ManagedServer();
        try
        {
            foreach (var user in users)
            {
                Native.AddUser(user.Name, user.Password);
                created.Add(user.Name);
            }
            var service = WindowsServiceAccounts.Resolve(users[0].Name);
            var oldNas = WindowsServiceAccounts.Resolve(users[1].Name);
            var newNas = WindowsServiceAccounts.Resolve(users[2].Name);
            smb = await WindowsSmbFixture.CreateAsync(oldNas.Name, oldNas.Sid, newNas.Name);
            Directory.CreateDirectory(directory);
            profileCreated = true;
            var folder = new DirectoryInfo(directory);
            var acl = folder.GetAccessControl();
            acl.SetOwner(owner);
            folder.SetAccessControl(acl);
            await new WindowsServiceProfile(owner.Value, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                { ServiceSid = service.Sid }.SaveAsync(directory);
            WindowsFtpCredentialStore.SetServiceCache(new(directory, owner.Value, service.Sid));
            var source = Path.Combine(directory, "source");
            Directory.CreateDirectory(source);
            const string marker = "Installed service credential change fixture";
            await File.WriteAllTextAsync(Path.Combine(source, "marker.txt"), marker);
            // Keep the real ZIP creation active while the owner publishes both new local bindings.
            var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
            await using (var large = File.Create(Path.Combine(source, "payload.bin")))
                for (var index = 0; index < 128; index++) await large.WriteAsync(payload);

            await server.Start();
            server.Job = new BackupJob
            {
                Name = "Managed live access fixture", Kind = DatabaseKind.FilesAndFolders, Source = source,
                DestinationDirectory = Path.Combine(directory, "local-copies"), ScheduleKind = ScheduleKind.Manual,
                AdditionalDestinations = [new() { StorageKind = StorageKind.NetworkShare, DestinationDirectory = smb.UncPath }],
                ManagedSecrets = new(true, [1])
            };
            var job = server.Job;
            var settings = new AgentSettings { AgentId = server.AgentId, Mode = AgentMode.Managed,
                ServerAddress = server.Address, CredentialKey = tokenKey, SyncIntervalSeconds = 15 };
            var connection = new ServerMonitoringSettings(server.Address, server.AgentId, tokenKey, server.Pin);
            var profile = new ManagedAgentProfile(directory, settings, connection);
            tokens.Save(tokenKey, "agent", server.Token);
            await tokens.ExportForServiceAsync(tokenKey);
            await new AgentSettingsStore(Path.Combine(directory, "agent.json")).SaveAsync(settings);
            await ServerMonitoringDispatcher.SaveSettingsAsync(directory, connection);

            string SaveNetwork(string name, string password)
            {
                var key = "live-smb-" + Guid.NewGuid().ToString("N");
                networkKeys.Add(key);
                secrets.Save(key, name, password);
                return key;
            }
            string SaveArchive(string password)
            {
                var key = archive.SaveNew(password);
                archiveKeys.Add(key);
                return key;
            }
            async Task<BackupJob> Bind(string archiveKey, string networkKey)
            {
                var resolved = job with
                {
                    ArchivePasswordKey = archiveKey,
                    AdditionalDestinations = [job.AdditionalDestinations![0] with
                    {
                        NetworkShare = new() { ShareRoot = NetworkShareSettings.Root(smb.UncPath), CredentialKey = networkKey }
                    }]
                };
                await WindowsFtpCredentialStore.ExportProfileForServiceAsync([resolved], []);
                await profile.Bindings.SaveAsync(job, new(job.Id, ManagedJobBindings.Scope(job), archiveKey, new Dictionary<int, string> { [1] = networkKey }));
                return resolved;
            }
            var oldPassword = "Zip-old-" + Guid.NewGuid().ToString("N");
            var newPassword = "Zip-new-" + Guid.NewGuid().ToString("N");
            var oldArchive = SaveArchive(oldPassword);
            await Bind(oldArchive, SaveNetwork(oldNas.Name, users[1].Password));
            var validNetwork = SaveNetwork(newNas.Name, users[2].Password);
            var incorrectNetwork = SaveNetwork(newNas.Name, "Wrong-" + Guid.NewGuid().ToString("N"));
            var newArchive = SaveArchive(newPassword);

            stage = "start-managed-service";
            server.Online = true;
            WindowsAgentService.Install(agent, directory, service.Name, owner.Value, users[0].Password);
            await WaitService(4);
            var control = new LocalAgentControlStore(directory);
            LocalAgentState? state = null;
            await Wait(async () => (state = await control.ReadAsync()) is { Phase: LocalAgentPhase.Running,
                ConfigurationRevision: 1, CanCheckStorages: true, CanRunManually: true } && server.Accepted,
                "Managed service did not accept the protected two-destination plan.");
            var session = state!;
            var queue = new LocalManualRunQueue(directory);
            var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
            var catalog = new BackupCatalog(profile.CatalogPath);
            stage = "change-while-running";
            await queue.EnqueueAsync(session.SessionId, job.Id);
            var firstRun = Guid.Empty;
            await Wait(async () =>
            {
                firstRun = (await history.ReadAsync()).SingleOrDefault(row => row.JobId == job.Id && row.Status == BackupRunStatus.Running)?.RunId ?? Guid.Empty;
                return firstRun != Guid.Empty;
            }, "The first backup never entered its running state.");
            firstId = firstRun;
            var newJob = await Bind(newArchive, validNetwork);
            Require((await history.ReadAsync()).Single(row => row.RunId == firstRun).Status == BackupRunStatus.Running
                && !Directory.EnumerateFiles(smb.BackingDirectory).Any(),
                "The fixture did not publish the access change before the first SMB delivery.");
            checks.Add("binding-published-before-first-smb-delivery-while-backup-running");

            async Task<BackupRun> Finished(Guid id)
            {
                BackupRun? row = null;
                await Wait(async () => (row = (await history.ReadAsync()).SingleOrDefault(run => run.RunId == id))
                    is { Status: not BackupRunStatus.Running }, "Backup did not finish.", 120);
                Require(row!.Status == BackupRunStatus.Succeeded && row.Destinations is { Count: 2 }
                    && row.Destinations.All(destination => destination.Record is not null), "Expected two successful destinations.");
                return row;
            }
            async Task AssertCopy(BackupRun run, string expectedSid, string password)
            {
                var records = run.Destinations!.Select(destination => destination.Record!).ToArray();
                var remote = smb.LocalPath(records[1].Backup.Location);
                Require(new FileInfo(remote).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value == expectedSid,
                    "SMB archive owner differs from the expected NAS identity.");
                foreach (var path in new[] { records[0].Backup.Location, remote })
                {
                    using var zip = new ZipFile(path) { Password = password };
                    var entry = zip.GetEntry("data/marker.txt");
                    Require(entry is { IsCrypted: true }, "Expected an AES-encrypted marker.");
                    using var reader = new StreamReader(zip.GetInputStream(entry));
                    Require(await reader.ReadToEndAsync() == marker, "The archive marker could not be restored.");
                }
            }
            stage = "verify-old-copy";
            var original = await Finished(firstRun);
            await AssertCopy(original, oldNas.Sid, oldPassword);
            checks.Add("running-backup-retained-old-smb-identity-and-zip-password");

            stage = "verify-fresh-access";
            var storageChecks = new LocalStorageCheckQueue(directory);
            async Task<StorageCheckReport> Probe(BackupJob requested) =>
                await storageChecks.WaitAsync(await storageChecks.EnqueueAsync(session.SessionId, requested));
            var good = await Probe(newJob);
            Require(good.ExecutionIdentity?.WindowsSid == service.Sid && good.Destinations is { Count: 2 }
                && good.Destinations.All(destination => destination.Succeeded), "The service did not confirm the new destination access.");
            var bad = await Probe(await Bind(newArchive, incorrectNetwork));
            Require(bad.ExecutionIdentity?.WindowsSid == service.Sid && bad.Destinations is { Count: 2 }
                && bad.Destinations[0].Succeeded && !bad.Destinations[1].Succeeded, "Incorrect credentials did not fail only the SMB destination.");
            await Bind(newArchive, validNetwork);
            checks.Add("fresh-service-probe-accepts-new-login-and-rejects-wrong-password-for-smb");

            stage = "verify-new-copy";
            await queue.EnqueueAsync(session.SessionId, job.Id);
            var nextRun = Guid.Empty;
            await Wait(async () =>
            {
                nextRun = (await history.ReadAsync()).SingleOrDefault(row => row.JobId == job.Id && row.RunId != firstRun)?.RunId ?? Guid.Empty;
                return nextRun != Guid.Empty;
            }, "A second backup did not start.");
            secondId = nextRun;
            await AssertCopy(await Finished(nextRun), newNas.Sid, newPassword);
            await AssertCopy(original, oldNas.Sid, oldPassword);
            Require((await catalog.ReadAsync()).Count == 4 && (await history.ReadAsync()).Count == 2
                && (await control.ReadAsync()) is { Phase: LocalAgentPhase.Running } current
                && current.SessionId == session.SessionId && current.ProcessId == session.ProcessId,
                "The service restarted or a backup was lost or replayed.");
            checks.Add("next-backup-used-new-smb-identity-and-zip-password-old-copy-stayed-readable-same-service-session");
            stage = "passed";
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "live-access-result.json"), JsonSerializer.Serialize(new
            {
                success = stage == "passed", stage, firstRun = firstId, secondRun = secondId, checks
            }));
            if (WindowsAgentService.QueryInfo() is { } info && info.Matches(agent, directory))
            {
                if (info.State != 1) { WindowsAgentService.Stop(agent, directory); await WaitService(1); }
                WindowsAgentService.Remove(agent, directory);
                await Wait(() => Task.FromResult(WindowsAgentService.QueryInfo() is null), "Live access fixture service removal failed.");
            }
            foreach (var key in networkKeys) secrets.Delete(key);
            foreach (var key in archiveKeys) archive.Delete(key);
            tokens.Delete(tokenKey);
            WindowsFtpCredentialStore.SetServiceCache(null);
            if (smb is not null) await smb.DisposeAsync();
            if (profileCreated && WindowsAgentService.QueryInfo() is null) Directory.Delete(directory, true);
            foreach (var name in created) Require(Native.DeleteUser(name) == 0, "Live access fixture account cleanup failed.");
        }
        return 0;
    }
}
