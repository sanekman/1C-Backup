using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using SPBackup.Core;
using ICSharpCode.SharpZipLib.Zip;

internal static class Program
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(message);
            await Task.Delay(200);
        }
    }
    private static Task WaitService(uint state) => Wait(() => Task.FromResult(WindowsAgentService.QueryInfo()?.State == state), "SCM did not reach state " + state);
    private static async Task WaitProcessExit(int id, string executable)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException) { /* Process has already exited. */ }
    }

    private static async Task TerminateAgent(int id, string executable)
    {
        using var process = Process.GetProcessById(id);
        Require(string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase),
            "Refusing to terminate a process other than the isolated published agent.");
        // Terminate only the parent: killing the tree here would hide orphaned dump processes.
        process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task AssertPostgreSqlRestores(PostgreSqlFixture postgres, BackupRecord copy, string password, string dump)
    {
        using (var zip = new ZipFile(copy.Backup.Location) { Password = password })
        {
            Require(zip.Count == 1 && zip[0].IsCrypted, "Service PostgreSQL archive must contain only the encrypted dump.");
            await using var input = zip.GetInputStream(0);
            await using var output = new FileStream(dump, FileMode.CreateNew, FileAccess.Write);
            await input.CopyToAsync(output);
        }
        await postgres.AssertRestoresAsync(dump);
    }

    private static async Task CaptureFailure(string directory, string output, string password, string archivePassword, string pgPassword)
    {
        var context = new Dictionary<string, object?>();
        async Task Capture(string name, Func<Task<object?>> read)
        {
            try { context[name] = await read(); }
            catch (Exception error) { context[name] = error.GetType().Name + ": " + error.Message; }
        }
        await Capture("service", () => Task.FromResult<object?>(WindowsAgentService.QueryInfo()));
        await Capture("agent", async () => await new LocalAgentControlStore(directory).ReadAsync());
        await Capture("history", async () => await new BackupRunHistory(Path.Combine(directory, "runs.json")).ReadAsync());
        foreach (var name in new[] { "jobs.json", "startup-schedule.json" })
            await Capture(name, async () => File.Exists(Path.Combine(directory, name)) ? await File.ReadAllTextAsync(Path.Combine(directory, name)) : null);
        var logs = Path.Combine(directory, "logs");
        if (Directory.Exists(logs))
            foreach (var file in Directory.EnumerateFiles(logs, "*.jsonl").Take(4))
                await Capture(Path.GetFileName(file), async () =>
                {
                    await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > 65536) stream.Seek(-65536, SeekOrigin.End);
                    using var reader = new StreamReader(stream);
                    return await reader.ReadToEndAsync();
                });
        var json = JsonSerializer.Serialize(context).Replace(password, "[redacted]").Replace(archivePassword, "[redacted]");
        if (!string.IsNullOrEmpty(pgPassword)) json = json.Replace(pgPassword, "[redacted]");
        await File.WriteAllTextAsync(Path.Combine(output, "failure-context.json"), json);
    }

    public static async Task<int> Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("ONECBACKUP_NATIVE_SERVICE_TEST") != "1" || args.Length != 2)
            throw new InvalidOperationException("Explicit isolated Windows CI opt-in, agent path and output folder are required.");
        var executable = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(local, "OneCBackup");
        var account = "ocb" + Guid.NewGuid().ToString("N")[..12];
        var password = "OcB!a9-" + Guid.NewGuid().ToString("N");
        var archivePassword = "CI encrypted service fixture password";
        var profileCreated = false; var userCreated = false;
        var archiveStore = new WindowsArchivePasswordStore(); string? archiveKey = null;
        var pgStore = new WindowsPostgreSqlCredentialStore(); var pgKey = Guid.NewGuid().ToString("N");
        PostgreSqlFixture? postgres = null;
        var checks = new List<string>(); string? failure = null;
        try
        {
            Require(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "An isolated administrator runner is required.");
            Require(WindowsAgentService.QueryInfo() is null, "Refusing to replace an existing agent service.");
            Require(!Directory.Exists(directory) && !File.Exists(directory), "Refusing to use an existing owner profile.");
            Require(File.Exists(executable), "Published agent not found.");
            postgres = new PostgreSqlFixture(); await postgres.StartAsync();
            pgStore.Save(pgKey, postgres.Username, postgres.Password);
            Native.AddUser(account, password); userCreated = true;
            var selected = WindowsServiceAccounts.Resolve(account);
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            Require(selected.Sid != sid, "The service must use a distinct non-interactive account.");
            var binary = new DirectoryInfo(Path.GetDirectoryName(executable)!);
            var binaryAcl = binary.GetAccessControl();
            binaryAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(selected.Sid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            binary.SetAccessControl(binaryAcl);
            Directory.CreateDirectory(directory); profileCreated = true;
            var acl = new DirectoryInfo(directory).GetAccessControl(); acl.SetOwner(new SecurityIdentifier(sid));
            new DirectoryInfo(directory).SetAccessControl(acl);
            var profile = new WindowsServiceProfile(sid, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), local) { ServiceSid = selected.Sid };
            await profile.SaveAsync(directory);
            var source = Path.Combine(directory, "fixture-base"); Directory.CreateDirectory(source);
            const string payload = "Service CI fixture, not a real 1C database";
            await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), payload);
            archiveKey = archiveStore.SaveNew(archivePassword);
            var manual = new BackupJob { Name = "disabled manual service fixture", Source = source, Enabled = false,
                OneCMethod = OneCBackupMethod.FileArchive, ArchivePasswordKey = archiveKey,
                DestinationDirectory = Path.Combine(directory, "copies", "manual"), ScheduleKind = ScheduleKind.Manual };
            var startup = manual with { Id = Guid.NewGuid(), Name = "startup service fixture", Enabled = true,
                DestinationDirectory = Path.Combine(directory, "copies", "startup"), ScheduleKind = ScheduleKind.SystemStartup };
            var pgJob = postgres.Job(Path.Combine(directory, "copies", "postgresql"), pgKey, archiveKey);
            var jobs = new[] { manual, startup, pgJob };
            var jobStore = new JobStore(Path.Combine(directory, "jobs.json")); await jobStore.SaveAsync(jobs);
            var originalJobs = await File.ReadAllTextAsync(Path.Combine(directory, "jobs.json"));
            WindowsFtpCredentialStore.SetServiceCache(new(directory, sid, selected.Sid));
            await WindowsFtpCredentialStore.ExportProfileForServiceAsync(jobs, []);
            WindowsAgentService.Install(executable, directory, selected.Name, sid, password);
            await WaitService(4);
            Require(WindowsAgentService.QueryInfo()!.Matches(executable, directory), "SCM registered a different binary or profile.");
            using (var registry = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + WindowsAgentServiceDefinition.Name))
                Require(registry?.GetValue("Start") is int start && start == 2, "The service is not configured to start automatically.");
            checks.Add("scm-distinct-account-automatic-start");
            var control = new LocalAgentControlStore(directory);
            LocalAgentState? state = null;
            await Wait(async () => (state = await control.ReadAsync()) is { Phase: LocalAgentPhase.Running, CanRunManually: true, CanCheckStorages: true }, "Non-interactive agent did not become ready.");
            var firstSession = state!.SessionId;
            Require(new BackupEngineCoordinator(directory).IsHeldElsewhere(), "Service did not exclusively own the execution profile.");
            checks.Add("non-interactive-host-and-engine-lock");
            var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
            var nextObservation = DateTimeOffset.MinValue;
            await Wait(async () =>
            {
                var current = await history.ReadAsync();
                if (current.Any(run => run.JobId == startup.Id && run.Status != BackupRunStatus.Running)) return true;
                if (DateTimeOffset.UtcNow >= nextObservation)
                {
                    nextObservation = DateTimeOffset.UtcNow.AddSeconds(5);
                    var observed = await control.ReadAsync();
                    Console.WriteLine(JsonSerializer.Serialize(new { phase = "waiting-for-startup", agent = observed?.Phase,
                        warning = observed?.Warning, error = observed?.Error, runs = current.Select(run => new { run.Status, run.Error }) })
                        .Replace(password, "[redacted]").Replace(archivePassword, "[redacted]"));
                }
                return false;
            }, "Startup job did not finish.");
            var startupRun = (await history.ReadAsync()).Single(run => run.JobId == startup.Id);
            Require(startupRun.Status == BackupRunStatus.Succeeded, "Startup backup failed under the service account: " + startupRun.Error);
            checks.Add("startup-backup-without-interactive-login");
            var storageChecks = new LocalStorageCheckQueue(directory);
            var beforeProbeHistory = await File.ReadAllBytesAsync(Path.Combine(directory, "runs.json"));
            var beforeProbeCatalog = await File.ReadAllBytesAsync(Path.Combine(directory, "copies.json"));
            var probeRequest = await storageChecks.EnqueueAsync(firstSession, manual);
            var probe = await storageChecks.WaitAsync(probeRequest);
            Require(probe.ExecutionIdentity is { WindowsSid: not null } probeIdentity && probeIdentity.WindowsSid == selected.Sid && probeIdentity.WindowsSid != sid,
                "Storage probe did not run under the actual distinct service identity.");
            Require(probe.TemporaryError is null && probe.Destinations.All(item => item.Succeeded), "Service storage access probe failed.");
            var afterProbeHistory = await File.ReadAllBytesAsync(Path.Combine(directory, "runs.json"));
            var afterProbeCatalog = await File.ReadAllBytesAsync(Path.Combine(directory, "copies.json"));
            Require(beforeProbeHistory.SequenceEqual(afterProbeHistory) && beforeProbeCatalog.SequenceEqual(afterProbeCatalog),
                "Storage access probe changed backup history or catalog.");
            Require((await control.ReadAsync()) is { SessionId: var probeSession, Phase: LocalAgentPhase.Running, RunsSchedules: true }
                && probeSession == firstSession, "Storage probe stopped or restarted the scheduler host.");
            checks.Add("storage-probe-under-service-identity-without-backup-or-schedule-stop");
            var queue = new LocalManualRunQueue(directory);
            var command = await queue.EnqueueAsync(firstSession, manual.Id);
            LocalManualRunReceipt? receipt = null;
            await Wait(async () => (receipt = await queue.ReadReceiptAsync(command.RequestId)) is { Phase: not LocalManualRunPhase.Accepted }, "Disabled manual-only job did not finish through the owner queue.");
            Require(receipt!.Phase == LocalManualRunPhase.Finished, "Manual command was rejected: " + receipt.Error);
            var runs = await history.ReadAsync();
            Require(runs.Count == 2 && runs.All(run => run.Status == BackupRunStatus.Succeeded), "Expected exactly two successful service backups.");
            checks.Add("owner-manual-queue-for-disabled-manual-job");
            var catalog = new BackupCatalog(Path.Combine(directory, "copies.json"));
            var records = await catalog.ReadAsync(); Require(records.Count == 2, "Encrypted copies were not registered.");
            var restore = Path.Combine(directory, "restore"); Directory.CreateDirectory(restore);
            foreach (var record in records)
            {
                var job = jobs.Single(job => job.Id == record.JobId);
                var extracted = await new OneCArchiveExtractor().ExtractAsync(record, job, restore);
                Require(await File.ReadAllTextAsync(extracted.Path) == payload, "Encrypted service copy did not restore correctly.");
            }
            checks.Add("delegated-dpapi-archive-password-and-restoration");
            var pgCommand = await queue.EnqueueAsync(firstSession, pgJob.Id);
            LocalManualRunReceipt? pgReceipt = null;
            await Wait(async () => (pgReceipt = await queue.ReadReceiptAsync(pgCommand.RequestId)) is { Phase: not LocalManualRunPhase.Accepted },
                "PostgreSQL service backup did not finish.");
            Require(pgReceipt!.Phase == LocalManualRunPhase.Finished, "PostgreSQL service command was rejected: " + pgReceipt.Error);
            var pgRun = (await history.ReadAsync()).Single(run => run.JobId == pgJob.Id);
            Require(pgRun.Status == BackupRunStatus.Succeeded, "PostgreSQL service backup failed: " + pgRun.Error);
            var pgCopy = (await catalog.ReadAsync()).Single(record => record.JobId == pgJob.Id);
            await AssertPostgreSqlRestores(postgres, pgCopy, archivePassword, Path.Combine(restore, "service-postgresql.dump"));
            checks.Add("postgresql-scram-with-delegated-password-and-restoration");
            WindowsAgentService.Stop(executable, directory); await WaitService(1);
            await WaitProcessExit(state!.ProcessId, executable);
            await Wait(() => Task.FromResult(!new BackupEngineCoordinator(directory).IsHeldElsewhere()), "SCM stop did not release the execution profile.");
            checks.Add("scm-stop-releases-engine-lock");
            WindowsAgentService.Start(executable, directory); await WaitService(4);
            await Wait(async () => (state = await control.ReadAsync()) is { Phase: LocalAgentPhase.Running } && state.SessionId != firstSession, "SCM restart reused the old session.");
            // The registered host polls schedules every 30 seconds; observe a full cycle after restart.
            await Wait(async () => (await control.ReadAsync()) is { Phase: LocalAgentPhase.Running } observed
                && observed.SessionId == state!.SessionId && observed.UpdatedUtc >= state.StartedUtc.AddSeconds(32),
                "Restarted service did not remain live through a schedule polling cycle.");
            Require((await history.ReadAsync()).Count == jobs.Length, "A same-boot service restart repeated a completed startup backup.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "jobs.json")) == originalJobs, "Service restart changed the saved jobs.");
            checks.Add("restart-preserves-history-and-does-not-repeat-startup");
            var beforeCrashHistory = await File.ReadAllBytesAsync(Path.Combine(directory, "runs.json"));
            var beforeCrashCatalog = await File.ReadAllBytesAsync(Path.Combine(directory, "copies.json"));
            var archiveHashes = new Dictionary<string, byte[]>();
            foreach (var record in await catalog.ReadAsync())
            {
                await using var archive = File.OpenRead(record.Backup.Location);
                archiveHashes[record.Backup.Location] = await System.Security.Cryptography.SHA256.HashDataAsync(archive);
            }
            var crashedSession = state!.SessionId;
            await TerminateAgent(state.ProcessId, executable);
            await WaitService(1);
            await Wait(() => Task.FromResult(!new BackupEngineCoordinator(directory).IsHeldElsewhere()),
                "Terminated process retained the engine lock.");
            WindowsAgentService.Start(executable, directory); await WaitService(4);
            await Wait(async () => (state = await control.ReadAsync()) is
                { Phase: LocalAgentPhase.Running, CanRunManually: true, CanCheckStorages: true }
                && state.SessionId != crashedSession, "Service did not recover with a fresh ready session after termination.");
            await Wait(async () => (await control.ReadAsync()) is { Phase: LocalAgentPhase.Running } observed
                && observed.SessionId == state!.SessionId && observed.UpdatedUtc >= state.StartedUtc.AddSeconds(32),
                "Service did not remain live after process termination.");
            var afterCrashHistory = await File.ReadAllBytesAsync(Path.Combine(directory, "runs.json"));
            var afterCrashCatalog = await File.ReadAllBytesAsync(Path.Combine(directory, "copies.json"));
            Require(beforeCrashHistory.SequenceEqual(afterCrashHistory) && beforeCrashCatalog.SequenceEqual(afterCrashCatalog),
                "Process termination or recovery changed completed history/catalog or repeated a startup backup.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "jobs.json")) == originalJobs,
                "Process termination or recovery changed saved jobs.");
            foreach (var entry in archiveHashes)
            {
                await using var archive = File.OpenRead(entry.Key);
                var hash = await System.Security.Cryptography.SHA256.HashDataAsync(archive);
                Require(entry.Value.SequenceEqual(hash), "Process termination or recovery changed a delivered archive.");
            }
            checks.Add("idle-process-termination-releases-lock-and-restart-preserves-copies");

            var completedRuns = await history.ReadAsync();
            var activeCrashSession = state!.SessionId;
            BackupRun interrupted;
            int dumpBackend;
            await using (await postgres.HoldBackupAsync())
            {
                var activeCommand = await queue.EnqueueAsync(activeCrashSession, pgJob.Id);
                dumpBackend = await postgres.WaitForBlockedDumpAsync();
                interrupted = (await history.ReadAsync()).Single(run => run.JobId == pgJob.Id && run.Status == BackupRunStatus.Running);
                Require((await queue.ReadReceiptAsync(activeCommand.RequestId))?.Phase == LocalManualRunPhase.Accepted,
                    "The actual blocked backup was not durably accepted.");
                var childId = Native.ChildProcessIds(state.ProcessId).Single(id =>
                {
                    using var child = Process.GetProcessById(id);
                    return string.Equals(child.MainModule?.FileName, postgres.DumpExecutablePath, StringComparison.OrdinalIgnoreCase);
                });
                using var dumpProcess = Process.GetProcessById(childId);
                Require(!dumpProcess.HasExited && Directory.Exists(interrupted.WorkDirectory),
                    "The real service dump was not active at the crash barrier.");
                Console.WriteLine(JsonSerializer.Serialize(new { phase = "active-backup-crash-barrier", runId = interrupted.RunId,
                    agentPid = state.ProcessId, dumpPid = childId, backendPid = dumpBackend }));
                await TerminateAgent(state.ProcessId, executable);
                // Keep the database barrier closed until the actual child exits. A restart must never
                // release the profile to a second executor while an orphan is still writing a dump.
                await dumpProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await WaitService(1);
                await Wait(() => Task.FromResult(!new BackupEngineCoordinator(directory).IsHeldElsewhere()),
                    "Terminating an active backup did not release the execution profile.");
            }
            await Wait(async () => !await postgres.IsDumpBackendPresentAsync(dumpBackend),
                "The terminated dump retained its PostgreSQL session after releasing the fixture barrier.");
            var staleCommand = await queue.EnqueueAsync(activeCrashSession, pgJob.Id);
            WindowsAgentService.Start(executable, directory); await WaitService(4);
            await Wait(async () => (state = await control.ReadAsync()) is
                { Phase: LocalAgentPhase.Running, CanRunManually: true, CanCheckStorages: true }
                && state.SessionId != activeCrashSession, "Service did not become ready after an active backup crash.");
            await Wait(async () => (await history.ReadAsync()).Single(run => run.RunId == interrupted.RunId).Status == BackupRunStatus.Interrupted,
                "The actually interrupted backup remained Running after recovery.");
            await Wait(async () => (await queue.ReadReceiptAsync(staleCommand.RequestId))?.Phase == LocalManualRunPhase.Rejected,
                "Restart accepted a command belonging to the crashed session.");
            await Wait(async () => (await control.ReadAsync()) is { Phase: LocalAgentPhase.Running } observed
                && observed.SessionId == state!.SessionId && observed.UpdatedUtc >= state.StartedUtc.AddSeconds(32),
                "Service did not remain live through a full polling cycle after an active backup crash.");
            var recoveredRuns = await history.ReadAsync();
            Require(recoveredRuns.Count == completedRuns.Count + 1 && recoveredRuns.All(run => run.Status != BackupRunStatus.Running),
                "Recovery replayed an interrupted/manual/startup backup or retained a Running record.");
            foreach (var completed in completedRuns)
                Require(JsonSerializer.Serialize(completed) == JsonSerializer.Serialize(recoveredRuns.Single(run => run.RunId == completed.RunId)),
                    "Active crash recovery changed a completed history record.");
            var recovered = recoveredRuns.Single(run => run.RunId == interrupted.RunId);
            Require(recovered.FinishedUtc is not null && !string.IsNullOrWhiteSpace(recovered.Error)
                && recovered.Backup is null && Directory.Exists(recovered.WorkDirectory),
                "Recovery did not retain the unfinished work with an honest interrupted outcome.");
            var recoveredCatalog = await File.ReadAllBytesAsync(Path.Combine(directory, "copies.json"));
            Require(beforeCrashCatalog.SequenceEqual(recoveredCatalog)
                && await File.ReadAllTextAsync(Path.Combine(directory, "jobs.json")) == originalJobs,
                "Active backup crash changed saved jobs or registered an unfinished dump.");
            foreach (var entry in archiveHashes)
            {
                await using var archive = File.OpenRead(entry.Key);
                var hash = await System.Security.Cryptography.SHA256.HashDataAsync(archive);
                Require(entry.Value.SequenceEqual(hash),
                    "Active backup crash changed a previously delivered archive.");
            }
            checks.Add("active-dump-parent-termination-stops-child-and-recovers-without-replay");
            var retry = await queue.EnqueueAsync(state!.SessionId, pgJob.Id);
            await Wait(async () => (await queue.ReadReceiptAsync(retry.RequestId)) is { Phase: not LocalManualRunPhase.Accepted },
                "Explicit backup after active crash did not finish.");
            Require((await queue.ReadReceiptAsync(retry.RequestId))?.Phase == LocalManualRunPhase.Finished,
                "The new service session rejected an explicit backup after the crash.");
            var finalRuns = await history.ReadAsync();
            Require(finalRuns.Count == jobs.Length + 2 && finalRuns.Count(run => run.Status == BackupRunStatus.Interrupted) == 1
                && finalRuns.Count(run => run.Status == BackupRunStatus.Succeeded) == jobs.Length + 1,
                "Explicit backup after crash produced unexpected history.");
            var newPgCopy = (await catalog.ReadAsync()).Single(record => record.JobId == pgJob.Id && record.Id != pgCopy.Id);
            await AssertPostgreSqlRestores(postgres, newPgCopy, archivePassword, Path.Combine(restore, "after-crash-postgresql.dump"));
            checks.Add("explicit-backup-after-active-crash-restores-real-data");
            WindowsAgentService.Stop(executable, directory); await WaitService(1);
            await WaitProcessExit(state!.ProcessId, executable);
            WindowsAgentService.Remove(executable, directory);
            await Wait(() => Task.FromResult(WindowsAgentService.QueryInfo() is null), "Service registration was not removed.");
            Require((await catalog.ReadAsync()).Count == jobs.Length + 1 && File.Exists(Path.Combine(directory, "jobs.json")), "Removing the service erased owner data.");
            checks.Add("service-removal-preserves-jobs-and-copies");
        }
        catch (Exception error)
        {
            failure = error.ToString().Replace(password, "[redacted]").Replace(archivePassword, "[redacted]")
                .Replace(postgres?.Password ?? "not-created-fixture-password", "[redacted]");
            if (profileCreated)
            {
                try { await CaptureFailure(directory, output, password, archivePassword, postgres?.Password ?? ""); }
                catch (Exception captureError) { failure += "\nFailure context: " + captureError.GetType().Name; }
            }
        }
        finally
        {
            try
            {
                if (profileCreated && WindowsAgentService.QueryInfo() is { } info && info.Matches(executable, directory))
                {
                    var process = await new LocalAgentControlStore(directory).ReadAsync();
                    if (info.State != 1) { WindowsAgentService.Stop(executable, directory); await WaitService(1); }
                    if (process is not null) await WaitProcessExit(process.ProcessId, executable);
                    WindowsAgentService.Remove(executable, directory);
                    await Wait(() => Task.FromResult(WindowsAgentService.QueryInfo() is null), "Service cleanup failed.");
                }
            }
            catch (Exception error) { failure = (failure ?? "") + "\nSCM cleanup: " + error.GetType().Name + ": " + error.Message; }
            WindowsFtpCredentialStore.SetServiceCache(null);
            try { pgStore.Delete(pgKey); if (postgres is not null) await postgres.DisposeAsync(); }
            catch (Exception error) { failure = (failure ?? "") + "\nPostgreSQL fixture cleanup: " + error.GetType().Name; }
            try { if (archiveKey is not null) archiveStore.Delete(archiveKey); }
            catch (Exception error) { failure = (failure ?? "") + "\nCredential cleanup: " + error.GetType().Name; }
            if (userCreated)
            {
                var deleted = Native.DeleteUser(account);
                if (deleted != 0) failure = (failure ?? "") + "\nAccount cleanup failed: " + deleted;
            }
            if (profileCreated)
            {
                try { Directory.Delete(directory, true); }
                catch (Exception error) { failure = (failure ?? "") + "\nProfile cleanup: " + error.GetType().Name; }
            }
        }
        var result = JsonSerializer.Serialize(new { success = failure is null, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"), checks, error = failure });
        await File.WriteAllTextAsync(Path.Combine(output, "service-result.json"), result);
        Console.WriteLine(result); return failure is null ? 0 : 1;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr DefaultHeap;
            public uint ModuleId, Threads, ParentProcessId;
            public int Priority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        public static IReadOnlyList<int> ChildProcessIds(int parent)
        {
            var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new List<int>();
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
                if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
                do { if (entry.ParentProcessId == parent) result.Add(checked((int)entry.ProcessId)); }
                while (Process32NextW(snapshot, ref entry));
                return result;
            }
            finally { CloseHandle(snapshot); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct User
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Name;
            [MarshalAs(UnmanagedType.LPWStr)] public string Password;
            public uint PasswordAge, Privilege;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Home;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public uint Flags;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Script;
        }
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserAdd(string? server, uint level, ref User user, out uint parameterError);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint NetUserDel(string? server, string name);
        public static void AddUser(string name, string password)
        {
            var user = new User { Name = name, Password = password, Privilege = 1, Flags = 0x10201, Comment = "Disposable SP Backup CI service account" };
            var code = NetUserAdd(null, 1, ref user, out _);
            if (code != 0) throw new Win32Exception((int)code, "Could not create the disposable CI account.");
        }
        public static uint DeleteUser(string name) => NetUserDel(null, name);
    }
}
