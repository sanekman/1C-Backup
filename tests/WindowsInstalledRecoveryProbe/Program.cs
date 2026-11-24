using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using SPBackup.Core;

if (args.Length is not (2 or 4) || !OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("This probe requires an installed agent on a disposable Windows CI runner.");

var installation = Path.GetFullPath(args[0]);
var agent = Path.Combine(installation, "OneCBackup.Agent.exe");
var core = typeof(BackupJob).Assembly.Location;
Require(File.Exists(agent) && await HashAsync(core) == await HashAsync(Path.Combine(installation, "SPBackup.Core.dll")),
    "The recovery probe must load the exact installed SPBackup.Core.");
var version = FileVersionInfo.GetVersionInfo(core).ProductVersion;
var report = Path.GetFullPath(args[1]);
Directory.CreateDirectory(report);
if (args.Length == 4)
{
    await HistoricalUncProbe.RunAsync(args[2], args[3], report, version, (profile, job) => RunAgentAsync(agent, profile, job));
    return;
}
var root = Path.Combine(Path.GetTempPath(), "spbackup-installed-recovery-" + Guid.NewGuid().ToString("N"));
var archiveStore = new WindowsArchivePasswordStore();
string? key = null;
var stage = "setup";
var checks = new List<string>();
Guid? fullRunId = null, incrementalRunId = null;
try
{
    Directory.CreateDirectory(root);
    var profile = NewFolder("original-profile");
    var source = NewFolder("source");
    var a = NewFolder("destination-a");
    var b = NewFolder("destination-b");
    Directory.CreateDirectory(Path.Combine(source, "selected"));
    await File.WriteAllTextAsync(Path.Combine(source, "changed.txt"), "initial");
    await File.WriteAllTextAsync(Path.Combine(source, "selected", "inside.txt"), "unchanged folder payload");
    await File.WriteAllTextAsync(Path.Combine(source, "unselected.txt"), "outside selection");
    var password = "Recovery fixture " + Guid.NewGuid().ToString("N");
    key = archiveStore.SaveNew(password);
    var job = new BackupJob
    {
        Name = "Installed recovery fixture", Kind = DatabaseKind.FilesAndFolders, Source = source,
        DestinationDirectory = a, AdditionalDestinations = [new() { DestinationDirectory = b }],
        ScheduleKind = ScheduleKind.Manual, FileBackupMode = FileBackupMode.Incremental, ArchivePasswordKey = key
    };
    await new JobStore(Path.Combine(profile, "jobs.json"), useLocalTime: true).SaveAsync([job]);
    var history = new BackupRunHistory(Path.Combine(profile, "runs.json"));
    var catalog = new BackupCatalog(Path.Combine(profile, "copies.json"));

    stage = "create-encrypted-chain";
    await RunAgentAsync(agent, profile, job.Id);
    var fullRun = (await history.ReadAsync()).Single();
    fullRunId = fullRun.RunId;
    var originalFolders = (await catalog.ReadAsync()).ToArray();
    Require(fullRun.Status == BackupRunStatus.Succeeded && originalFolders.Length == 2 && originalFolders.All(
        item => item.Backup.FileSnapshot is { IsFull: true } && item.Backup.ArchivePasswordKey == key), "Expected a full encrypted archive at both destinations.");
    await File.WriteAllTextAsync(Path.Combine(source, "changed.txt"), "incremental contents");
    await RunAgentAsync(agent, profile, job.Id);
    var incrementalRun = (await history.ReadAsync()).Single(run => run.RunId != fullRun.RunId);
    incrementalRunId = incrementalRun.RunId;
    var copies = await catalog.ReadAsync();
    Require(incrementalRun.Status == BackupRunStatus.Succeeded && copies.Count == 4 &&
        copies.Count(item => item.Backup.FileSnapshot is { IsFull: false }) == 2, "Expected one increment at both destinations.");
    var originalHashes = new Dictionary<string, string>();
    foreach (var copy in copies)
        originalHashes.Add(copy.Backup.Location, await HashAsync(copy.Backup.Location));
    var fullA = copies.Single(item => Path.GetDirectoryName(item.Backup.Location) == a && item.Backup.FileSnapshot!.IsFull);
    var pointB = copies.Single(item => Path.GetDirectoryName(item.Backup.Location) == b && !item.Backup.FileSnapshot!.IsFull);
    var removed = NewFolder("held-back");
    foreach (var copy in copies.Where(item => item.Id != fullA.Id && item.Id != pointB.Id))
        File.Move(copy.Backup.Location, Path.Combine(removed, copy.Id.ToString("N") + ".zip"));
    checks.Add("installed-agent-created-two-encrypted-full-and-two-incremental-archives");

    stage = "restore-selected-paths";
    var storage = new FileBackupStorage();
    var restorer = new FileBackupRestorer(storage, catalog, NewFolder("restorer-work"), archiveStore);
    string[] selection = ["changed.txt", "selected"];
    var noConsent = NewFolder("no-consent");
    await ExpectFailure<IOException>(() => restorer.RestoreAsync(pointB, job, noConsent, selectedPaths: selection));
    AssertEmpty(noConsent);
    var restored = await restorer.RestoreAsync(pointB, job, NewFolder("selected-restore"), selectedPaths: selection, allowOtherDestinations: true);
    await VerifyRestoredAsync(restored, includeUnselected: false);
    Require(restored.Archives == 2, "Both physical chain parts must be read.");
    checks.Add("selected-file-and-folder-restored-from-full-in-a-and-increment-in-b-with-consent");

    stage = "missing-base-and-cancellation";
    var hiddenBase = Path.Combine(removed, "full-a.zip");
    File.Move(fullA.Backup.Location, hiddenBase);
    try
    {
        var missing = NewFolder("missing-base-restore");
        await ExpectFailure<IOException>(() => restorer.RestoreAsync(pointB, job, missing, selectedPaths: selection, allowOtherDestinations: true));
        AssertEmpty(missing);
    }
    finally { File.Move(hiddenBase, fullA.Backup.Location); }
    var cancelled = NewFolder("cancelled-restore");
    using (var cancellation = new CancellationTokenSource())
    {
        var triggered = false;
        await ExpectFailure<OperationCanceledException>(() => restorer.RestoreAsync(pointB, job, cancelled, cancellation.Token,
            progress: new InlineProgress(progress =>
            {
                if (progress.Phase != FileRestorePhase.VerifyingFiles) return;
                triggered = true;
                cancellation.Cancel();
            }), selectedPaths: selection, allowOtherDestinations: true));
        Require(triggered, "Cancellation did not occur during actual restore verification.");
    }
    AssertEmpty(cancelled);
    Require(await HashAsync(fullA.Backup.Location) == originalHashes[fullA.Backup.Location]
        && await HashAsync(pointB.Backup.Location) == originalHashes[pointB.Backup.Location], "Restore altered the chain archives.");
    checks.Add("missing-base-and-in-flight-cancellation-leave-no-result-and-preserve-original-archives");

    stage = "export-and-restore-without-profile";
    var kit = await new FileRecoveryKit(storage).ExportAsync(pointB, job, catalog, NewFolder("transfer"), permitOtherDestinations: true);
    var summary = await FileRecoveryKit.InspectAsync(kit);
    var metadata = await File.ReadAllTextAsync(Path.Combine(kit, FileRecoveryKit.ManifestName));
    Require(summary.Encrypted && summary.Archives == 2 && !metadata.Contains(source) && !metadata.Contains(key) && !metadata.Contains(password),
        "Expected an encrypted two-archive kit without profile references or passwords.");
    await VerifyHashesAsync(copies, removed, originalHashes);
    var heldProfile = Path.Combine(root, "held-original-profile");
    Directory.Move(profile, heldProfile);
    Directory.Move(source, Path.Combine(root, "held-source"));
    Directory.Move(a, Path.Combine(root, "held-a"));
    Directory.Move(b, Path.Combine(root, "held-b"));
    archiveStore.Delete(key);
    key = null;
    var emptyProfile = NewFolder("clean-profile");
    var portableTarget = NewFolder("portable-target");
    try { await FileRecoveryKit.RestoreAsync(kit, portableTarget, emptyProfile, "Wrong fixture password"); }
    catch (Exception error) when (error is InvalidDataException or ZipException or CryptographicException) { checks.Add("wrong-password-rejected"); }
    Require(checks.Contains("wrong-password-rejected"), "The kit accepted an incorrect password.");
    AssertEmpty(portableTarget);
    AssertEmpty(emptyProfile);
    var portable = await FileRecoveryKit.RestoreAsync(kit, portableTarget, emptyProfile, password);
    await VerifyRestoredAsync(portable, includeUnselected: true);
    AssertEmpty(emptyProfile);
    checks.Add("encrypted-kit-restored-without-source-destinations-catalog-or-vault-key");
    var kitIncrement = Path.Combine(kit, summary.SnapshotId.ToString("N") + ".zip");
    var heldIncrement = Path.Combine(removed, "kit-increment.zip");
    File.Move(kitIncrement, heldIncrement);
    try
    {
        var missing = NewFolder("missing-increment-restore");
        await ExpectFailure<FileNotFoundException>(() => FileRecoveryKit.RestoreAsync(kit, missing, emptyProfile, password));
        AssertEmpty(missing);
        AssertEmpty(emptyProfile);
    }
    finally { File.Move(heldIncrement, kitIncrement); }
    checks.Add("missing-kit-increment-leaves-no-restored-output");
    stage = "passed";
}
finally
{
    await File.WriteAllTextAsync(Path.Combine(report, "recovery-result.json"), JsonSerializer.Serialize(new
    {
        success = stage == "passed", stage, version, fullRunId, incrementalRunId, checks
    }));
    if (key is not null) archiveStore.Delete(key);
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

string NewFolder(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
static void AssertEmpty(string directory) =>
    Require(!Directory.EnumerateFileSystemEntries(directory).Any(), "Restore left output after an error or cancellation.");

static async Task ExpectFailure<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T) { return; }
    catch (InvalidDataException) when (typeof(T) == typeof(IOException)) { return; }
    throw new InvalidOperationException("Restore unexpectedly succeeded.");
}
static async Task<string> HashAsync(string path)
{
    await using var file = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(file));
}
static async Task VerifyHashesAsync(IReadOnlyList<BackupRecord> copies, string held, Dictionary<string, string> hashes)
{
    foreach (var copy in copies)
    {
        var location = File.Exists(copy.Backup.Location) ? copy.Backup.Location : Path.Combine(held, copy.Id.ToString("N") + ".zip");
        Require(await HashAsync(location) == hashes[copy.Backup.Location], "An original backup changed.");
    }
}
static async Task VerifyRestoredAsync(RestoredFileBackup restored, bool includeUnselected)
{
    Require(restored.Files == (includeUnselected ? 3 : 2)
        && await File.ReadAllTextAsync(Path.Combine(restored.Directory, "changed.txt")) == "incremental contents"
        && await File.ReadAllTextAsync(Path.Combine(restored.Directory, "selected", "inside.txt")) == "unchanged folder payload"
        && File.Exists(Path.Combine(restored.Directory, "unselected.txt")) == includeUnselected, "Restored file set or contents differ.");
    if (includeUnselected)
        Require(await File.ReadAllTextAsync(Path.Combine(restored.Directory, "unselected.txt")) == "outside selection", "Portable restore changed an unselected file.");
}
static async Task RunAgentAsync(string agent, string profile, Guid jobId)
{
    var start = new ProcessStartInfo(agent) { UseShellExecute = false };
    foreach (var argument in new[] { "--run-job", jobId.ToString("D"), "--data-dir", profile, "--quiet" })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new IOException("Could not start installed agent.");
    try
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        Require(process.ExitCode == 0, "The installed agent did not create the test backup.");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    }
}

sealed class InlineProgress(Action<FileRestoreProgress> report) : IProgress<FileRestoreProgress>
{
    public void Report(FileRestoreProgress value) => report(value);
}
