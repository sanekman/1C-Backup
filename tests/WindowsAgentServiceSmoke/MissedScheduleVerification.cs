using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SPBackup.Core;

internal static partial class Program
{
    private static async Task<int> VerifyMissedScheduleAsync(string agent, string output)
    {
        var profileDirectory = DefaultOwnerProfile;
        agent = Path.GetFullPath(agent);
        Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && File.Exists(agent)
            && WindowsAgentService.QueryInfo() is null && !Directory.Exists(profileDirectory),
            "Missed-schedule fixture requires a clean disposable Windows runner and an installed agent.");
        Directory.CreateDirectory(output);
        var account = "spbs" + Guid.NewGuid().ToString("N")[..10];
        var password = "Sc!8a-" + Guid.NewGuid().ToString("N");
        var owner = WindowsIdentity.GetCurrent().User!;
        var userCreated = false;
        var profileCreated = false;
        var stage = "prepare";
        Guid? completedRun = null;
        DateTimeOffset? stopped = null;
        DateTimeOffset? restarted = null;
        DateTimeOffset? scheduled = null;

        try
        {
            Native.AddUser(account, password);
            userCreated = true;
            var selected = WindowsServiceAccounts.Resolve(account);
            Directory.CreateDirectory(profileDirectory);
            profileCreated = true;
            var directory = new DirectoryInfo(profileDirectory);
            var acl = directory.GetAccessControl();
            acl.SetOwner(owner);
            directory.SetAccessControl(acl);
            var profile = new WindowsServiceProfile(owner.Value,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { ServiceSid = selected.Sid };
            await profile.SaveAsync(profileDirectory);

            var source = Path.Combine(profileDirectory, "schedule-source");
            Directory.CreateDirectory(source);
            const string contents = "Installed agent catch-up fixture";
            await File.WriteAllTextAsync(Path.Combine(source, "example.txt"), contents);
            var now = DateTimeOffset.UtcNow;
            var due = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(3);
            scheduled = due;
            var skip = new BackupJob
            {
                Name = "Skip missed slot", Kind = DatabaseKind.FilesAndFolders, Source = source,
                DestinationDirectory = Path.Combine(profileDirectory, "skip"), Enabled = true,
                ScheduleKind = ScheduleKind.Daily, DailyAt = TimeOnly.FromDateTime(due.UtcDateTime), TimeZoneId = "UTC"
            };
            var catchUp = skip with
            {
                Id = Guid.NewGuid(), Name = "Catch up missed slot", CatchUpWithinMinutes = 5,
                DestinationDirectory = Path.Combine(profileDirectory, "catch-up")
            };
            BackupJob[] jobs = [skip, catchUp];
            await new JobStore(Path.Combine(profileDirectory, "jobs.json")).SaveAsync(jobs);
            var controls = new LocalAgentControlStore(profileDirectory);
            var history = new BackupRunHistory(Path.Combine(profileDirectory, "runs.json"));
            var catalog = new BackupCatalog(Path.Combine(profileDirectory, "copies.json"));
            var cursor = Path.Combine(profileDirectory, "calendar-schedule.json");

            async Task<LocalAgentState> Started()
            {
                await WaitService(4);
                LocalAgentState? state = null;
                await Wait(async () => (state = await controls.ReadAsync()) is { Phase: LocalAgentPhase.Running, RunsSchedules: true },
                    "The installed service did not start its scheduler.");
                return state!;
            }

            stage = "observe-and-stop";
            WindowsAgentService.Install(agent, profileDirectory, selected.Name, owner.Value, password);
            var before = await Started();
            await Wait(() => Task.FromResult(File.Exists(cursor)), "The running scheduler did not persist its next slots.");
            using (var state = JsonDocument.Parse(await File.ReadAllTextAsync(cursor)))
                foreach (var job in jobs)
                    Require(state.RootElement.GetProperty(job.Id.ToString()).GetProperty("Next").GetDateTimeOffset() == due,
                        "Unexpected persisted schedule slot.");
            WindowsAgentService.Stop(agent, profileDirectory);
            await WaitService(1);
            await WaitProcessExit(before.ProcessId, agent);
            stopped = DateTimeOffset.UtcNow;
            Require(stopped < due && (await history.ReadAsync()).Count == 0, "Service must stop before the due time.");

            stage = "restart-after-missed-slot";
            var downtime = due.AddSeconds(3) - DateTimeOffset.UtcNow;
            if (downtime > TimeSpan.Zero) await Task.Delay(downtime);
            await WaitService(1);
            restarted = DateTimeOffset.UtcNow;
            WindowsAgentService.Start(agent, profileDirectory);
            var after = await Started();
            Require(after.SessionId != before.SessionId, "The agent did not start a new service session.");
            await Wait(async () => (await history.ReadAsync()).Any(run => run.JobId == catchUp.Id && run.Status == BackupRunStatus.Succeeded),
                "The missed slot was not executed after restarting the service.");
            var missed = await LocalBackupDispatcher.ReadMissedRunsAsync(profileDirectory, jobs);
            Require(missed.GetValueOrDefault(skip.Id) == new MissedScheduledRun(due, false)
                && missed.GetValueOrDefault(catchUp.Id) == new MissedScheduledRun(due, true),
                "The saved skip and catch-up outcomes differ from the selected policies.");
            var run = (await history.ReadAsync()).Single();
            var copy = (await catalog.ReadAsync()).Single();
            Require(run.JobId == catchUp.Id && copy.JobId == catchUp.Id, "Unexpected backup from the skipped slot.");
            using (var archive = ZipFile.OpenRead(copy.Backup.Location))
            using (var reader = new StreamReader(archive.Entries.Single(entry => entry.Name == "example.txt").Open()))
                Require(await reader.ReadToEndAsync() == contents, "The scheduled archive did not contain the source bytes.");
            completedRun = run.RunId;

            stage = "restart-again-in-catch-up-window";
            WindowsAgentService.Stop(agent, profileDirectory);
            await WaitService(1);
            await WaitProcessExit(after.ProcessId, agent);
            Require(DateTimeOffset.UtcNow < due.AddMinutes(5), "Second restart must occur while catch-up still applies.");
            WindowsAgentService.Start(agent, profileDirectory);
            var second = await Started();
            Require(second.SessionId != after.SessionId, "The second service startup did not create a new session.");
            await Wait(async () => (await controls.ReadAsync()) is { Phase: LocalAgentPhase.Running } live
                && live.SessionId == second.SessionId && live.UpdatedUtc >= second.StartedUtc.AddSeconds(32),
                "The second service session did not remain active through a full scheduling cycle.");
            Require((await history.ReadAsync()).Single().RunId == completedRun && (await catalog.ReadAsync()).Single().Id == copy.Id,
                "Restarting inside the same catch-up window duplicated the backup.");
            stage = "passed";
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "missed-schedule-result.json"), JsonSerializer.Serialize(new
            {
                success = stage == "passed", stage, scheduled, stopped, restarted, completedRun
            }));
            if (WindowsAgentService.QueryInfo() is { } info && info.Matches(agent, profileDirectory))
            {
                if (info.State != 1) { WindowsAgentService.Stop(agent, profileDirectory); await WaitService(1); }
                WindowsAgentService.Remove(agent, profileDirectory);
                await Wait(() => Task.FromResult(WindowsAgentService.QueryInfo() is null), "Schedule fixture service removal failed.");
            }
            if (profileCreated && WindowsAgentService.QueryInfo() is null) Directory.Delete(profileDirectory, true);
            if (userCreated) Require(Native.DeleteUser(account) == 0, "Schedule fixture account cleanup failed.");
        }
        return 0;
    }
}
