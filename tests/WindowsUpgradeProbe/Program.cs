using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
#if LEGACY
using OneCBackup.Core;
#else
using SPBackup.Core;
#endif

if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
    || Environment.GetEnvironmentVariable("SPBACKUP_UPGRADE_SMOKE") != "1")
    throw new InvalidOperationException("Requires an explicitly enabled isolated Windows CI runner.");
if (args.Length != 3) throw new ArgumentException("Expected phase, private state directory and installed application directory.");
var state = Path.GetFullPath(args[1]); var installation = Path.GetFullPath(args[2]);
// Framework-dependent probe; additional dependencies come from the verified installed package.
AssemblyLoadContext.Default.Resolving += (_, name) => {
    var path = Path.Combine(installation, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
return await Probe.RunAsync(args[0], state);

internal static class Probe
{
    private const string Password = "isolated upgrade fixture 🗝";
    private const string Payload = "SP Backup upgrade: original file-base bytes\n";
    private static string Profile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneCBackup");
    private static string FileInProfile(string name) => Path.Combine(Profile, name);
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Fixture(Guid JobId, string Key, Guid? CopyId = null, string? CopyPath = null, string? CopySha256 = null,
        Dictionary<string, string>? ProfileHashes = null);

    public static async Task<int> RunAsync(string phase, string state)
    {
        if (phase is not ("prepare" or "snapshot" or "verify-one" or "verify-two" or "verify-after-uninstall" or "cleanup"))
            throw new ArgumentException("Unknown upgrade verification phase.");
        var metadata = Path.Combine(state, "fixture.json");
        Directory.CreateDirectory(state);
        var owner = WindowsIdentity.GetCurrent().User!.Value;
        var cache = new WindowsServiceCredentialCache(Profile, owner);
        var vault = new WindowsArchivePasswordStore();
        Fixture fixture;
        if (phase == "prepare")
        {
#if !LEGACY
            throw new InvalidOperationException("Preparation must use the published pre-rename assembly.");
#else
            Require(typeof(BackupJob).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                == "0.2.0-alpha.25+2c0dfb62aa3a8244956479d2f0d1b7a09cc50692", "Unexpected legacy assembly revision.");
            Require(!File.Exists(metadata) && !File.Exists(FileInProfile("jobs.json")), "Refusing to replace an existing profile/fixture.");
            var source = Path.Combine(state, "source"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), Payload);
            var key = vault.SaveNew(Password);
            fixture = new(Guid.NewGuid(), key);
            // Persist ownership before subsequent steps so cleanup can remove only this credential.
            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(fixture));
            cache.Save("Archive", key, new NetworkCredential("archive", Password));
            var job = new BackupJob { Id = fixture.JobId, Name = "Published alpha.25 upgrade fixture", Source = source,
                DestinationDirectory = Path.Combine(state, "copies"), TemporaryDirectory = Path.Combine(state, "work"),
                OneCMethod = OneCBackupMethod.FileArchive, ScheduleKind = ScheduleKind.Manual, ArchivePasswordKey = key, KeepLast = 4 };
            await new JobStore(FileInProfile("jobs.json")).SaveAsync([job]);
            await BackupJobTransfer.ExportAsync(job, Path.Combine(state, "legacy-job.json"));
#endif
        }
        else
        {
            fixture = JsonSerializer.Deserialize<Fixture>(await File.ReadAllTextAsync(metadata))!;
            if (phase == "cleanup")
            {
                vault.Delete(fixture.Key); cache.Delete("Archive", fixture.Key);
                Console.WriteLine("Owned upgrade credentials removed; profile and copies retained."); return 0;
            }
            Require(await vault.GetAsync(fixture.Key) == Password, "Old Windows Vault credential is unavailable.");
            Require(cache.Read("Archive", fixture.Key)?.Password == Password, "Old DPAPI service credential cannot be decrypted.");
            var jobs = await new JobStore(FileInProfile("jobs.json")).LoadAsync();
            Require(jobs.Count == 1 && jobs[0].Id == fixture.JobId, "Job identity changed.");
            var job = jobs[0];
            Require(job.ArchivePasswordKey == fixture.Key, "Job credential binding changed.");
            var imported = await BackupJobTransfer.ImportAsync(Path.Combine(state, "legacy-job.json"));
            Require(imported.Id != fixture.JobId && !imported.Enabled && imported.Source == job.Source && imported.ArchivePasswordKey == fixture.Key, "Legacy settings export is incompatible.");
            var expected = phase is "snapshot" or "verify-one" ? 1 : 2;
            var records = await new BackupCatalog(FileInProfile("copies.json")).ReadAsync();
            var history = await new BackupRunHistory(FileInProfile("runs.json")).ReadAsync();
            Require(records.Count == expected && history.Count == expected && history.All(run => run.Status == BackupRunStatus.Succeeded),
                "Expected successful backups and their original history.");
            if (phase == "snapshot")
            {
                var paths = new[] { FileInProfile("jobs.json"), FileInProfile("runs.json"), FileInProfile("copies.json") }
                    .Concat(Directory.GetFiles(FileInProfile("service-secrets"), "*.bin"));
                fixture = fixture with { CopyId = records[0].Id, CopyPath = records[0].Backup.Location,
                    CopySha256 = Hash(records[0].Backup.Location), ProfileHashes = paths.ToDictionary(path => path, Hash) };
                await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(fixture));
            }
            Require(records.Any(copy => copy.Id == fixture.CopyId) && Hash(fixture.CopyPath!) == fixture.CopySha256,
                "Original copy was removed, replaced or changed.");
            if (phase == "verify-one")
                foreach (var item in fixture.ProfileHashes!) Require(Hash(item.Key) == item.Value, "Installer changed a profile file.");
            foreach (var record in records)
            {
                var parent = Path.Combine(state, "restored-" + phase + "-" + record.Id.ToString("N")); Directory.CreateDirectory(parent);
                var restored = await new OneCArchiveExtractor().ExtractAsync(record, job, parent);
                Require(await File.ReadAllTextAsync(restored.Path) == Payload, "Restored content differs from original bytes.");
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, phase,
            assembly = typeof(BackupJob).Assembly.GetName().Name, jobId = fixture.JobId }));
        return 0;
    }
}
