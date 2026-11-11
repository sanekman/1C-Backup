using SPBackup.Core;
using ICSharpCode.SharpZipLib.Zip;

internal static partial class Program
{
    private static async Task<string> VerifyLiveSettingsAsync(string directory, List<string> checks)
    {
        var control = new LocalAgentControlStore(directory);
        var state = (await control.ReadAsync())!;
        Require(state.CanUpdateLocalJobs, "The installed service does not advertise live profile updates.");
        var store = new JobStore(Path.Combine(directory, "jobs.json"), useLocalTime: true);
        var before = await store.LoadAsync();
        var added = new BackupJob { Name = "Disabled protected live-settings fixture", Kind = DatabaseKind.PostgreSql,
            Source = "fixture", DestinationDirectory = Path.Combine(directory, "live-settings-copies"),
            Enabled = false, ScheduleKind = ScheduleKind.Manual, PostgreSql = new()
            { PgDumpPath = Path.Combine(directory, "fixture-pg_dump.exe"), PgRestorePath = Path.Combine(directory, "fixture-pg_restore.exe"), Tls = new() } };
        var archiveStore = new WindowsArchivePasswordStore();
        var original = before.First(j => j.Kind == DatabaseKind.OneCFile && j.OneCMethod == OneCBackupMethod.FileArchive && j.ScheduleKind == ScheduleKind.Manual);
        var oldPassword = await archiveStore.GetAsync(original.ArchivePasswordKey!);
        const string newPassword = "Live settings encrypted ZIP fixture password";
        var newKey = archiveStore.SaveNew(newPassword);
        var copyJob = original with { Id = Guid.NewGuid(), Name = "Live settings archive fixture", ArchivePasswordKey = newKey,
            DestinationDirectory = Path.Combine(directory, "live-archive-copies") };
        IReadOnlyList<BackupJob> changed = [..before, added, copyJob];
        var updates = new LocalJobUpdates(directory);
        await updates.ApplyAsync(state, before, changed);
        Require((await store.LoadAsync()).Any(j => j.Id == added.Id && j.PostgreSql?.Tls is not null), "The service did not persist the protected profile.");
        Require(!(await File.ReadAllTextAsync(Path.Combine(directory, "jobs.json"))).Contains(added.Id.ToString()), "A protected live update leaked into the legacy profile.");
        var queue = new LocalManualRunQueue(directory);
        await queue.EnqueueAsync(state.SessionId, copyJob.Id);
        var history = new BackupRunHistory(Path.Combine(directory, "runs.json"));
        await Wait(async () => (await history.ReadAsync()).Any(run => run.JobId == copyJob.Id && run.Status != BackupRunStatus.Running), "The live archive job did not finish.");
        var run = (await history.ReadAsync()).Single(run => run.JobId == copyJob.Id);
        Require(run.Status == BackupRunStatus.Succeeded, "New archive password was not usable by the running service: "+run.Error);
        var record = (await new BackupCatalog(Path.Combine(directory, "copies.json")).ReadAsync()).Single(copy => copy.JobId == copyJob.Id);
        using (var zip = new ZipFile(record.Backup.Location) { Password = newPassword })
        {
            Require(zip.Count == 1 && zip[0].IsCrypted && zip[0].Name == "1Cv8.1CD", "The live password backup is not the expected encrypted infobase file.");
            using var reader = new StreamReader(zip.GetInputStream(zip[0]));
            Require(await reader.ReadToEndAsync() == await File.ReadAllTextAsync(Path.Combine(copyJob.Source, "1Cv8.1CD")), "Live settings ZIP bytes differ from the fixture source.");
        }
        Require(await archiveStore.GetAsync(original.ArchivePasswordKey!) == oldPassword, "A new archive password replaced the password of old copies.");
        await updates.ApplyAsync(state, changed, before);
        var after = (await control.ReadAsync())!;
        Require(after.SessionId == state.SessionId && after.ProcessId == state.ProcessId && after.Phase == LocalAgentPhase.Running,
            "Changing settings restarted or stopped the service.");
        Require(LocalJobUpdates.Hash(await store.LoadAsync()) == LocalJobUpdates.Hash(before), "Restoring the fixture profile failed.");
        checks.Add("protected-job-settings-saved-and-removed-by-running-service-without-restart");
        checks.Add("new-archive-password-applied-by-distinct-service-account-with-verified-zip-and-old-password-preserved");
        return newKey;
    }
}
