using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SPBackup.Core;

internal static class HistoricalUncProbe
{
    private sealed record Fixture(string WindowsSid, string PreviousDestination);
    private sealed record Prepared(Guid JobId, Guid RunId, string Sha256);
    private const string Content = "Restored from the former UNC destination.";

    internal static async Task RunAsync(string phase, string lab, string output, string? version, Func<string, Guid, Task> runAgent)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(Path.Combine(lab, "unc-fixture.json")))!;
        using var identity = WindowsIdentity.GetCurrent();
        Require(identity.User?.Value == fixture.WindowsSid
            && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            "The restore must run under the fixture's standard Windows user.");
        var profile = Directory.CreateDirectory(Path.Combine(lab, "owner-profile")).FullName;
        var jobStore = new JobStore(Path.Combine(profile, "jobs.json"), useLocalTime: true);
        var catalog = new BackupCatalog(Path.Combine(profile, "copies.json"));
        var signal = Path.Combine(lab, "prepared.json");
        if (phase == "prepare-unc")
        {
            var source = Directory.CreateDirectory(Path.Combine(lab, "source")).FullName;
            await File.WriteAllTextAsync(Path.Combine(source, "original.txt"), Content);
            var previous = new BackupJob
            {
                Name = "Former UNC fixture", Kind = DatabaseKind.FilesAndFolders, Source = source,
                DestinationDirectory = fixture.PreviousDestination, StorageKind = StorageKind.NetworkShare, ScheduleKind = ScheduleKind.Manual
            };
            await jobStore.SaveAsync([previous]);
            await runAgent(profile, previous.Id);
            var run = (await new BackupRunHistory(Path.Combine(profile, "runs.json")).ReadAsync()).Single();
            var point = (await catalog.ReadAsync()).Single();
            Require(run.Status == BackupRunStatus.Succeeded && point.Backup.FileSnapshot is { IsFull: true }
                && point.Backup.Location.StartsWith(fixture.PreviousDestination + "\\", StringComparison.OrdinalIgnoreCase),
                "The installed agent did not register a copy on the former share.");
            var current = previous with { StorageKind = StorageKind.LocalDirectory, DestinationDirectory = Path.Combine(lab, "new-destination") };
            await jobStore.SaveAsync([current]);
            await File.WriteAllTextAsync(signal, JsonSerializer.Serialize(new Prepared(current.Id, run.RunId, await HashAsync(point.Backup.Location))));
            return;
        }

        Require(phase is "restore-unc" or "deny-unc", "Unknown UNC test phase.");
        var prepared = JsonSerializer.Deserialize<Prepared>(await File.ReadAllTextAsync(signal))!;
        var job = (await jobStore.LoadAsync()).Single();
        var selected = (await catalog.ReadAsync()).Single();
        var historical = HistoricalFileCopy.CreateReadJob(selected, job);
        Require(prepared.JobId == job.Id && historical is { StorageKind: StorageKind.NetworkShare, NetworkShare: null }
            && historical.DestinationDirectory == fixture.PreviousDestination, "The registered copy no longer resolves to its previous UNC path.");
        var restorer = new FileBackupRestorer(new FileBackupStorage(), catalog, Path.Combine(lab, "restore-work"));
        var target = Directory.CreateDirectory(Path.Combine(lab, phase)).FullName;
        if (phase == "deny-unc")
        {
            try { await restorer.RestoreAsync(selected, historical!, target); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Require(!Directory.EnumerateFileSystemEntries(target).Any(), "Denied restoration left an incomplete result.");
                return;
            }
            throw new InvalidOperationException("Restore unexpectedly read the UNC archive without share permission.");
        }

        var writeProbe = Path.Combine(fixture.PreviousDestination, "read-only-proof-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handle = new FileStream(writeProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose);
            throw new InvalidOperationException("The test share unexpectedly allows this user to write.");
        }
        catch (UnauthorizedAccessException) { }
        var restored = await restorer.RestoreAsync(selected, historical!, target);
        Require(restored.Files == 1 && await File.ReadAllTextAsync(Path.Combine(restored.Directory, "original.txt")) == Content
            && await HashAsync(selected.Backup.Location) == prepared.Sha256 && !Directory.Exists(job.DestinationDirectory),
            "The former archive or current destination changed, or the restored contents differ.");
        await File.WriteAllTextAsync(Path.Combine(output, "historical-unc-result.json"), JsonSerializer.Serialize(new
        {
            success = true, version, runId = prepared.RunId, restoredFiles = restored.Files,
            standardWindowsUser = true, readOnlyShare = true, originalHashPreserved = true
        }));
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
