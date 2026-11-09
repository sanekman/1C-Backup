using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using SPBackup.Core;

internal static partial class Program
{
    private static async Task<int> VerifyServiceSwitchAsync(string agent, string output)
    {
        Require(WindowsAgentService.QueryInfo() is null && !Directory.Exists(DefaultOwnerProfile), "Account-switch fixture requires no existing service or owner profile.");
        Directory.CreateDirectory(output); agent = Path.GetFullPath(agent);
        var owner = WindowsIdentity.GetCurrent().User!.Value;
        var accounts = new[] { "spba" + Guid.NewGuid().ToString("N")[..10], "spbb" + Guid.NewGuid().ToString("N")[..10] };
        var passwords = new[] { "Sw!8a-" + Guid.NewGuid().ToString("N"), "Sw!8b-" + Guid.NewGuid().ToString("N") };
        var created = new List<string>(); var checks = new List<string>(); string? failure = null;
        var accountSids = new List<string>(); var completedCopies = 0;
        var archiveStore = new WindowsArchivePasswordStore(); string? archiveKey = null;
        try
        {
            Directory.CreateDirectory(DefaultOwnerProfile);
            var acl = new DirectoryInfo(DefaultOwnerProfile).GetAccessControl(); acl.SetOwner(new SecurityIdentifier(owner));
            new DirectoryInfo(DefaultOwnerProfile).SetAccessControl(acl);
            var source = Path.Combine(DefaultOwnerProfile, "switch-source"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "Account switch fixture");
            const string archivePassword = "switch fixture archive password";
            archiveKey = archiveStore.SaveNew(archivePassword);
            var job = new BackupJob { Source = source, Name = "Account switch", OneCMethod = OneCBackupMethod.FileArchive,
                DestinationDirectory = Path.Combine(DefaultOwnerProfile, "copies"), ArchivePasswordKey = archiveKey,
                Enabled = true, ScheduleKind = ScheduleKind.SystemStartup };
            await new JobStore(Path.Combine(DefaultOwnerProfile, "jobs.json")).SaveAsync([job]);
            var jobsJson = await File.ReadAllBytesAsync(Path.Combine(DefaultOwnerProfile, "jobs.json"));
            var history = new BackupRunHistory(Path.Combine(DefaultOwnerProfile, "runs.json"));
            var catalog = new BackupCatalog(Path.Combine(DefaultOwnerProfile, "copies.json"));
            var control = new LocalAgentControlStore(DefaultOwnerProfile);
            BackupRecord? first = null; string? firstHash = null;
            foreach (var index in new[] { 0, 1 })
            {
                Native.AddUser(accounts[index], passwords[index]); created.Add(accounts[index]);
                var selected = WindowsServiceAccounts.Resolve(accounts[index]);
                var profile = new WindowsServiceProfile(owner, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { ServiceSid = selected.Sid };
                await profile.SaveAsync(DefaultOwnerProfile);
                WindowsFtpCredentialStore.SetServiceCache(new(DefaultOwnerProfile, owner, selected.Sid));
                await WindowsFtpCredentialStore.ExportProfileForServiceAsync([job], await catalog.ReadAsync());
                var binary = new DirectoryInfo(Path.GetDirectoryName(agent)!); var binaryAcl = binary.GetAccessControl();
                binaryAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(selected.Sid), FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                binary.SetAccessControl(binaryAcl);
                WindowsAgentService.Install(agent, DefaultOwnerProfile, selected.Name, owner, passwords[index]);
                await WaitService(4); LocalAgentState? state = null;
                await Wait(async () => (state = await control.ReadAsync()) is { Phase: LocalAgentPhase.Running, CanCheckSources: true, CanRunManually: true }, "Switched service did not become ready.");
                var report = await new LocalSourceCheckQueue(DefaultOwnerProfile).WaitAsync(await new LocalSourceCheckQueue(DefaultOwnerProfile).EnqueueAsync(state!.SessionId, job));
                Require(report.Succeeded && report.ExecutionIdentity?.WindowsSid == selected.Sid && selected.Sid != owner,
                    "The source probe did not identify the newly selected service account.");
                accountSids.Add(selected.Sid);
                if (index == 0)
                {
                    await Wait(async () => (await history.ReadAsync()).Count(run => run.Status == BackupRunStatus.Succeeded) == 1, "Initial startup backup failed.");
                    first = (await catalog.ReadAsync()).Single(); firstHash = await HashAsync(first.Backup.Location);
                }
                else
                {
                    await Wait(async () => (await control.ReadAsync()) is { Phase: LocalAgentPhase.Running } live
                        && live.SessionId == state.SessionId && live.UpdatedUtc >= live.StartedUtc.AddSeconds(32),
                        "The switched account did not remain live through a scheduler cycle.");
                    Require((await history.ReadAsync()).Count == 1, "Changing the service account replayed the startup backup.");
                    var queue = new LocalManualRunQueue(DefaultOwnerProfile);
                    var command = await queue.EnqueueAsync(state.SessionId, job.Id);
                    await Wait(async () => (await queue.ReadReceiptAsync(command.RequestId)) is { Phase: LocalManualRunPhase.Finished }, "Explicit switched-account backup did not finish.");
                    var completedRuns = await history.ReadAsync();
                    Require(completedRuns.Count == 2 && completedRuns.All(run => run.Status == BackupRunStatus.Succeeded)
                        && firstHash == await HashAsync(first!.Backup.Location), "The switched-account backup failed, lost or repeated an old backup.");
                    var copies = await catalog.ReadAsync();
                    Require(copies.Count == 2, "Both the original and switched-account copies must be registered.");
                    completedCopies = copies.Count;
                    foreach (var copy in copies)
                    {
                        using var zip = new ZipFile(copy.Backup.Location) { Password = archivePassword };
                        using var reader = new StreamReader(zip.GetInputStream(0));
                        Require(await reader.ReadToEndAsync() == "Account switch fixture", "Old/new encrypted copy did not restore after account switch.");
                    }
                }
                var afterJobs = await File.ReadAllBytesAsync(Path.Combine(DefaultOwnerProfile, "jobs.json"));
                Require(jobsJson.SequenceEqual(afterJobs), "Changing the account changed the saved plan.");
                WindowsAgentService.Stop(agent, DefaultOwnerProfile); await WaitService(1); await WaitProcessExit(state.ProcessId, agent);
                WindowsAgentService.Remove(agent, DefaultOwnerProfile);
                await Wait(() => Task.FromResult(WindowsAgentService.QueryInfo() is null), "Account-switch service removal failed.");
            }
            Require(accountSids.Distinct(StringComparer.Ordinal).Count() == 2, "The account switch did not use two distinct verified SIDs.");
            checks.Add("two-distinct-service-identities-reported-and-protected-password-reexported");
            checks.Add("account-switch-preserves-startup-slot-plan-history-and-old-and-new-encrypted-copies");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            try
            {
                if (WindowsAgentService.QueryInfo() is { } info && info.Matches(agent, DefaultOwnerProfile))
                {
                    if (info.State != 1) { WindowsAgentService.Stop(agent, DefaultOwnerProfile); await WaitService(1); }
                    WindowsAgentService.Remove(agent, DefaultOwnerProfile);
                    await Wait(() => Task.FromResult(WindowsAgentService.QueryInfo() is null), "Switch cleanup failed.");
                }
                WindowsFtpCredentialStore.SetServiceCache(null);
                if (archiveKey is not null) archiveStore.Delete(archiveKey);
                if (WindowsAgentService.QueryInfo() is null && Directory.Exists(DefaultOwnerProfile)) Directory.Delete(DefaultOwnerProfile, true);
                foreach (var account in created) Require(Native.DeleteUser(account) == 0, "Owned switch account cleanup failed.");
            }
            catch (Exception error) { failure = (failure ?? "") + " Cleanup: " + error.Message; }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "service-switch-result.json"), JsonSerializer.Serialize(new {
            success = failure is null, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"), accountSids, completedCopies, checks, error = failure }));
        return failure is null ? 0 : 1;
    }
}
