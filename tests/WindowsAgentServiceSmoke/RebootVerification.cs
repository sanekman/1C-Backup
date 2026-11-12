using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using SPBackup.Core;

internal static partial class Program
{
    // This file runs only on a disposable Windows stand, under the same owner before/after boot.
    // It never initiates a restart itself. The controller must explicitly request the OS restart.
    private sealed record RebootCheckpoint(int Format, Guid BootId, string OwnerSid, string Executable,
        string AgentSha256, string ProbeSha256, string Profile, string Account, string ArchiveKey, string PgKey,
        PostgreSqlFixture.Snapshot PostgreSql, WindowsSmbFixture.Snapshot Smb, Guid SessionId,
        BackupRun InterruptedRun, LocalManualRunRequest Command, Guid StartupJobId, Guid ManualJobId, Guid PgJobId,
        IReadOnlyList<BackupRun> CompletedRuns, string JobsJson, byte[] ProtectedJobs,
        Dictionary<string, byte[]> ArchiveHashes, IReadOnlyList<string> Checks, string? Revision);

    private static string CheckpointPath(string output) => Path.Combine(output, "reboot-checkpoint.bin");
    private static async Task<string> HashAsync(string path)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input));
    }
    private static async Task ArmRebootAsync(string executable, string directory, string output, string account,
        string archiveKey, string pgKey, PostgreSqlFixture postgres, WindowsSmbFixture smb, LocalAgentState state,
        BackupRun interrupted, LocalManualRunRequest command, Guid startupId, Guid manualId, Guid pgId,
        IReadOnlyList<BackupRun> completed, string jobsJson, Dictionary<string, byte[]> archives, List<string> checks)
    {
        var owner = WindowsIdentity.GetCurrent().User!;
        var checkpoint = new RebootCheckpoint(1, SystemBootSession.ReadId(), owner.Value, executable,
            await HashAsync(executable), await HashAsync(typeof(Program).Assembly.Location), directory, account,
            archiveKey, pgKey, postgres.Capture(), smb.Capture(), state.SessionId, interrupted, command,
            startupId, manualId, pgId, completed, jobsJson,
            await File.ReadAllBytesAsync(Path.Combine(directory, "jobs.json.protected.json")), archives, checks,
            Environment.GetEnvironmentVariable("GITHUB_SHA"));
        var path = CheckpointPath(output);
        Require(!File.Exists(path) && !File.Exists(Path.Combine(output, "reboot-ready.json")), "Refusing to replace an earlier reboot checkpoint.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        // Create the encrypted state without inheriting write access from the service-binary directory.
        var security = new FileSecurity(); security.SetOwner(owner); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        using (var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096,
            FileOptions.WriteThrough, security))
        {
            await stream.WriteAsync(encrypted); stream.Flush(flushToDisk: true);
        }
        var ready = new { format = 1, bootId = checkpoint.BootId, sessionId = state.SessionId,
            runId = interrupted.RunId, agentPid = state.ProcessId, expiresUtc = DateTimeOffset.UtcNow.AddSeconds(75),
            revision = checkpoint.Revision };
        await File.WriteAllTextAsync(Path.Combine(output, "reboot-ready.json"), JsonSerializer.Serialize(ready));
        Console.WriteLine("Real pg_dump is blocked; reboot checkpoint is durable. Waiting up to 75 seconds for an explicit controller restart.");
        await Task.Delay(TimeSpan.FromSeconds(75));
        File.Delete(Path.Combine(output, "reboot-ready.json"));
        throw new TimeoutException("No OS restart occurred at the armed backup barrier. This is not a successful reboot test.");
    }

    private static async Task<int> VerifyRebootAsync(string agent, string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        var encrypted = await File.ReadAllBytesAsync(CheckpointPath(output));
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        RebootCheckpoint saved;
        try { saved = JsonSerializer.Deserialize<RebootCheckpoint>(plain) ?? throw new InvalidDataException("Missing reboot checkpoint."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Require(saved.Format == 1 && saved.OwnerSid == WindowsIdentity.GetCurrent().User?.Value,
            "Resume must use the original fixture owner and supported checkpoint format.");
        Require(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "Administrator resume is required.");
        Require(saved.BootId != SystemBootSession.ReadId(), "Windows did not reboot. A process/service restart cannot pass this test.");
        Require(string.Equals(Path.GetFullPath(agent), saved.Executable, StringComparison.OrdinalIgnoreCase)
            && await HashAsync(saved.Executable) == saved.AgentSha256
            && await HashAsync(typeof(Program).Assembly.Location) == saved.ProbeSha256,
            "The tested agent/probe changed across boot.");
        Require(saved.Profile == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneCBackup")
            && (await WindowsServiceProfile.LoadAsync(saved.Profile))?.OwnerSid == saved.OwnerSid,
            "The original isolated owner profile is unavailable.");
        // All ownership checks above precede changes or cleanup. Wrong-machine/same-boot attempts leave the stand intact.
        var checks = saved.Checks.ToList();
        PostgreSqlFixture? postgres = null; WindowsSmbFixture? smb = null;
        string? failure = null;
        try
        {
            WindowsAgentService.AssertOwned(saved.Executable, saved.Profile);
            smb = WindowsSmbFixture.Reopen(saved.Smb);
            var credential = await new WindowsPostgreSqlCredentialStore().GetAsync(saved.PgKey);
            postgres = PostgreSqlFixture.Reopen(saved.PostgreSql, credential.Password);
            await WaitService(4); // Deliberately do not call Start: SCM automatic start is part of the assertion.
            var control = new LocalAgentControlStore(saved.Profile);
            LocalAgentState? state = null;
            await Wait(async () => (state = await control.ReadAsync()) is
                { Phase: LocalAgentPhase.Running, CanRunManually: true, CanCheckStorages: true }
                && state.SessionId != saved.SessionId, "The service did not start automatically with a new session after boot.");
            Require(new BackupEngineCoordinator(saved.Profile).IsHeldElsewhere(), "The restarted service does not own its profile.");
            checks.Add("real-os-boot-changed-and-scm-started-without-interactive-service-login");
            var history = new BackupRunHistory(Path.Combine(saved.Profile, "runs.json"));
            var queue = new LocalManualRunQueue(saved.Profile);
            await Wait(async () => (await history.ReadAsync()).Count(run => run.JobId == saved.StartupJobId && run.Status == BackupRunStatus.Succeeded) == 2,
                "The startup job did not run exactly once for the new Windows boot.");
            await Wait(async () => (await history.ReadAsync()).Single(run => run.RunId == saved.InterruptedRun.RunId).Status != BackupRunStatus.Running,
                "The pre-boot active backup remained Running.");
            await Wait(async () => (await queue.ReadReceiptAsync(saved.Command.RequestId)) is { Phase: LocalManualRunPhase.Interrupted },
                "The consumed pre-boot manual command retained a live receipt.");
            var stale = await queue.EnqueueAsync(saved.SessionId, saved.PgJobId);
            await Wait(async () => (await queue.ReadReceiptAsync(stale.RequestId)) is { Phase: LocalManualRunPhase.Rejected },
                "The new service accepted a command from the old boot session.");
            await Wait(async () => (await control.ReadAsync()) is { Phase: LocalAgentPhase.Running } current
                && current.SessionId == state!.SessionId && current.UpdatedUtc >= state.StartedUtc.AddSeconds(32),
                "The recovered service did not remain live through a scheduler polling cycle.");
            var recovered = await history.ReadAsync();
            Require(recovered.Count == saved.CompletedRuns.Count + 2 && recovered.All(run => run.Status != BackupRunStatus.Running),
                "OS recovery repeated a manual backup or startup job, or left a live run.");
            var interrupted = recovered.Single(run => run.RunId == saved.InterruptedRun.RunId);
            Require(interrupted.Status is BackupRunStatus.Cancelled or BackupRunStatus.Interrupted
                && interrupted.Backup is null && interrupted.FinishedUtc is not null,
                "The unfinished dump was reported as a completed backup.");
            foreach (var previous in saved.CompletedRuns)
                Require(JsonSerializer.Serialize(previous) == JsonSerializer.Serialize(recovered.Single(run => run.RunId == previous.RunId)),
                    "OS reboot changed an already completed history record.");
            Require(await File.ReadAllTextAsync(Path.Combine(saved.Profile, "jobs.json")) == saved.JobsJson
                && (await File.ReadAllBytesAsync(Path.Combine(saved.Profile, "jobs.json.protected.json"))).SequenceEqual(saved.ProtectedJobs),
                "OS reboot changed ordinary or protected jobs.");
            var catalog = new BackupCatalog(Path.Combine(saved.Profile, "copies.json"));
            Require((await catalog.ReadAsync()).Count == saved.ArchiveHashes.Count + 1, "An unfinished dump was registered or a delivered copy was lost.");
            foreach (var previous in saved.ArchiveHashes)
            {
                await using var archive = File.OpenRead(previous.Key);
                Require((await SHA256.HashDataAsync(archive)).SequenceEqual(previous.Value), "OS reboot changed a previously delivered archive.");
            }
            checks.Add("os-reboot-preserves-jobs-history-catalog-archive-bytes-and-does-not-replay-manual-command");
            // The private PostgreSQL cluster is a fixture, not an automatically installed server.
            // Restart it only after verifying the agent's own automatic recovery without it.
            await postgres.RestartAfterBootAsync();
            var restore = Path.Combine(output, "reboot-restores"); Directory.CreateDirectory(restore);
            const string archivePassword = "CI encrypted service fixture password";
            var oldPg = (await catalog.ReadAsync()).Single(record => record.JobId == saved.PgJobId);
            await AssertPostgreSqlRestores(postgres, oldPg, archivePassword, Path.Combine(restore, "before-boot.dump"));
            var retry = await queue.EnqueueAsync(state!.SessionId, saved.PgJobId);
            await Wait(async () => (await queue.ReadReceiptAsync(retry.RequestId)) is { Phase: LocalManualRunPhase.Finished },
                "A new explicit service backup did not finish after Windows reboot.");
            var newPg = (await catalog.ReadAsync()).Single(record => record.JobId == saved.PgJobId && record.Id != oldPg.Id);
            await AssertPostgreSqlRestores(postgres, newPg, archivePassword, Path.Combine(restore, "after-boot.dump"));
            var startup = (await catalog.ReadAsync()).Where(record => record.JobId == saved.StartupJobId).ToArray();
            Require(startup.Length == 2, "Expected one startup copy for each of two actual OS boots.");
            foreach (var copy in startup)
            {
                using var zip = new ZipFile(copy.Backup.Location) { Password = archivePassword };
                Require(zip.Count == 1 && zip[0].IsCrypted && zip[0].Name == "1Cv8.1CD", "The startup archive format changed.");
                using var reader = new StreamReader(zip.GetInputStream(0));
                Require(await reader.ReadToEndAsync() == "Service CI fixture, not a real 1C database", "The startup copy did not restore source bytes.");
            }
            var jobs = await new JobStore(Path.Combine(saved.Profile, "jobs.json")).LoadAsync();
            var probe = new LocalStorageCheckQueue(saved.Profile);
            var handle = await probe.EnqueueAsync(state.SessionId, jobs.Single(job => job.DestinationDirectory == saved.SmbRootUnc()));
            var probeResult = await probe.WaitAsync(handle);
            Require(probeResult.ExecutionIdentity?.WindowsSid == saved.Smb.ServiceSid && probeResult.TemporaryError is null
                && probeResult.Destinations.All(destination => destination.Succeeded),
                "SCM identity or actual UNC storage access changed across boot.");
            checks.Add("before-and-after-boot-encrypted-sql-and-startup-copies-restore-and-service-identity-survives");
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        finally
        {
            // Clean up only resources revalidated against this encrypted checkpoint.
            try
            {
                if (WindowsAgentService.QueryInfo() is { } info && info.Matches(saved.Executable, saved.Profile))
                {
                    var state = await new LocalAgentControlStore(saved.Profile).ReadAsync();
                    if (info.State != 1) { WindowsAgentService.Stop(saved.Executable, saved.Profile); await WaitService(1); }
                    if (state is not null) await WaitProcessExit(state.ProcessId, saved.Executable);
                    WindowsAgentService.Remove(saved.Executable, saved.Profile);
                    await Wait(() => Task.FromResult(WindowsAgentService.QueryInfo() is null), "Reboot fixture service cleanup failed.");
                }
            }
            catch (Exception error) { failure = (failure ?? "") + "\nService cleanup: " + error.Message; }
            try { if (smb is not null) await smb.DisposeAsync(); }
            catch (Exception error) { failure = (failure ?? "") + "\nSMB cleanup: " + error.Message; }
            try { if (postgres is not null) await postgres.DisposeAsync(); }
            catch (Exception error) { failure = (failure ?? "") + "\nPostgreSQL cleanup: " + error.Message; }
            if (WindowsAgentService.QueryInfo() is null)
            {
                try
                {
                    new WindowsPostgreSqlCredentialStore().Delete(saved.PgKey);
                    new WindowsArchivePasswordStore().Delete(saved.ArchiveKey);
                    Require(WindowsServiceAccounts.Resolve(saved.Account).Sid == saved.Smb.ServiceSid, "The fixture account SID changed; refusing deletion.");
                    Require(Native.DeleteUser(saved.Account) == 0, "Reboot fixture account cleanup failed.");
                    Directory.Delete(saved.Profile, recursive: true);
                }
                catch (Exception error) { failure = (failure ?? "") + "\nOwner profile cleanup: " + error.Message; }
            }
        }
        var result = JsonSerializer.Serialize(new { success = failure is null, revision = saved.Revision,
            previousBootId = saved.BootId, bootId = SystemBootSession.ReadId(), checks, error = failure });
        await File.WriteAllTextAsync(Path.Combine(output, "reboot-result.json"), result);
        Console.WriteLine(result);
        return failure is null ? 0 : 1;
    }

    private static string SmbRootUnc(this RebootCheckpoint checkpoint) => @"\\" + Environment.MachineName + @"\SPBackupServiceCI_" + checkpoint.Smb.Id;
}
