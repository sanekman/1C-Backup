using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SPBackup.Core;

internal static partial class Program
{
    private sealed class VolumeNoDatabase : IBackupExecutor
    {
        public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) =>
            throw new InvalidOperationException("A file fixture must not call a database executor.");
    }

    // This fixture creates and formats ONLY its new VHD file, never a physical disk.
    private static async Task<int> VerifyVolumesAsync(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var root = Path.Combine(output, "owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var disk = Path.Combine(root, "quota.vhdx");
        var letter = Enumerable.Range('R', 9).Select(code => ((char)code).ToString())
            .First(value => !DriveInfo.GetDrives().Any(drive => drive.Name.StartsWith(value + ":", StringComparison.OrdinalIgnoreCase)));
        var volume = letter + @":\";
        var account = "spbq" + Guid.NewGuid().ToString("N")[..10];
        var password = "Quota!9-" + Guid.NewGuid().ToString("N");
        var created = false; var attached = false; string? failure = null;
        var checks = new List<string>();
        async Task Command(string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var text = await stdout + await stderr;
            if (process.ExitCode != 0) throw new IOException(executable + " failed: " + text.Replace(password, "[redacted]"));
        }
        async Task DiskPart(string commands)
        {
            var script = Path.Combine(root, "diskpart.txt"); await File.WriteAllTextAsync(script, commands);
            await Command("diskpart.exe", "/s", script);
        }
        void Grant(string path, string sid)
        {
            var directory = new DirectoryInfo(path); var acl = directory.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
        }
        try
        {
            Require(!File.Exists(disk) && !Directory.Exists(volume), "The isolated VHD or drive letter is already in use.");
            await DiskPart($"create vdisk file=\"{disk}\" maximum=128 type=expandable\nselect vdisk file=\"{disk}\"\nattach vdisk\ncreate partition primary\nformat fs=ntfs quick label=SPBQuota\nassign letter={letter}\n");
            attached = true;
            Require(new DriveInfo(volume) is { DriveFormat: "NTFS", VolumeLabel: "SPBQuota" }, "The owned NTFS VHD did not mount.");
            Native.AddUser(account, password); created = true;
            var identity = WindowsServiceAccounts.Resolve(account);
            Grant(root, identity.Sid); Grant(volume, identity.Sid);
            if (!LogonVolumeUser(account, Environment.MachineName, password, 2, 0, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
                var data = Path.Combine(source, "data.bin"); await File.WriteAllBytesAsync(data, RandomNumberGenerator.GetBytes(4096));
                var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
                var storage = new FileBackupStorage();
                var runner = new LocalBackupRunner(new VolumeNoDatabase(), new BackupCompletion(storage, catalog),
                    new BackupRunHistory(Path.Combine(root, "runs.json")), Path.Combine(root, "work"));
                var job = new BackupJob { Name = "Real NTFS capacity", Kind = DatabaseKind.FilesAndFolders, Source = source,
                    DestinationDirectory = Path.Combine(volume, "copies"), KeepLast = 20, Enabled = false, ScheduleKind = ScheduleKind.Manual,
                    FileBackupMode = FileBackupMode.Incremental, FullBackupEvery = 365,
                    AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "healthy"), KeepLast = 20 }] };
                async Task<BackupRun> Run() => await WindowsIdentity.RunImpersonatedAsync(token, async () =>
                {
                    Require(WindowsIdentity.GetCurrent().User?.Value == identity.Sid && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
                        "Quota verification must run as the owned non-administrator.");
                    return await runner.RunAsync(job).WaitAsync(TimeSpan.FromSeconds(60));
                });
                Require((await Run()).Status == BackupRunStatus.Succeeded, "Initial mirrored backup failed.");
                var initial = (await catalog.ReadAsync()).ToArray();
                var hashes = new Dictionary<string, string>();
                foreach (var record in initial) hashes.Add(record.Backup.Location, await HashAsync(record.Backup.Location));
                async Task VerifyOld()
                {
                    foreach (var pair in hashes) Require(await HashAsync(pair.Key) == pair.Value, "A failed delivery removed or changed an old archive.");
                }
                async Task VerifyRestored(BackupRun run)
                {
                    var expected = await HashAsync(data);
                    foreach (var delivery in run.Destinations!.Where(item => item.Record is not null))
                    {
                        var destination = Path.Combine(root, "restores"); Directory.CreateDirectory(destination);
                        var restored = await new FileBackupRestorer(storage, catalog, Path.Combine(root, "restore-work"))
                            .RestoreAsync(delivery.Record!, job, destination);
                        Require(await HashAsync(Path.Combine(restored.Directory, "data.bin")) == expected, "Recovered bytes differ after a real capacity failure.");
                        Require(!delivery.Record!.Backup.FileSnapshot!.IsFull, "Capacity failure caused an unscheduled full backup.");
                    }
                }
                await Command("fsutil.exe", "quota", "enforce", letter + ":");
                await Command("fsutil.exe", "quota", "modify", letter + ":", "1048576", "1048576", identity.Name);
                await File.WriteAllBytesAsync(data, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
                Require(new DriveInfo(volume).TotalFreeSpace > 32 * 1024 * 1024, "Quota fixture unexpectedly exhausted the physical volume.");
                var quota = await Run();
                Require(quota.Status == BackupRunStatus.PartiallySucceeded && quota.Destinations!.Count(item => item.Error is not null) == 1,
                    "The real NTFS quota did not fail only its own destination: " + quota.Error);
                await VerifyOld(); await VerifyRestored(quota);
                checks.Add("real-nonadmin-ntfs-quota-isolated-from-physical-free-space-and-healthy-destination-restores");
                await Command("fsutil.exe", "quota", "disable", letter + ":");
                var recoveredQuota = await Run(); Require(recoveredQuota.Status == BackupRunStatus.Succeeded, "Removing the quota did not recover delivery: " + recoveredQuota.Error);
                await VerifyOld(); await VerifyRestored(recoveredQuota);
                var filler = Path.Combine(volume, "owned-filler.bin");
                await using (var fill = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var block = RandomNumberGenerator.GetBytes(65536);
                    while (new DriveInfo(volume).AvailableFreeSpace > 1024 * 1024) await fill.WriteAsync(block);
                    await fill.FlushAsync(); fill.Flush(true);
                }
                await File.WriteAllBytesAsync(data, RandomNumberGenerator.GetBytes(4 * 1024 * 1024));
                var full = await Run();
                Require(full.Status == BackupRunStatus.PartiallySucceeded && full.Destinations!.Count(item => item.Error is not null) == 1,
                    "The real full volume did not fail only its own destination: " + full.Error);
                await VerifyOld(); await VerifyRestored(full);
                checks.Add("real-filled-owned-vhd-preserves-old-archives-and-independent-incremental-delivery");
                File.Delete(filler);
                var recoveredFull = await Run(); Require(recoveredFull.Status == BackupRunStatus.Succeeded, "Freeing the owned volume did not recover delivery: " + recoveredFull.Error);
                await VerifyOld(); await VerifyRestored(recoveredFull);
                Require((await catalog.ReadAsync()).Where(record => record.Backup.FileSnapshot?.IsFull == true).Count() == 2,
                    "Recovery introduced another full basis instead of repairing the original chain.");
                checks.Add("quota-and-full-volume-reconnect-backfills-chain-and-restores-both-targets-without-unscheduled-full");
            }
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message.Replace(password, "[redacted]"); }
        finally
        {
            try
            {
                // Selection is exclusively by the newly generated file path; never select a disk number.
                if (attached || File.Exists(disk)) await DiskPart($"select vdisk file=\"{disk}\"\ndetach vdisk\n");
                if (created) Require(Native.DeleteUser(account) == 0, "Owned quota account cleanup failed.");
                Directory.Delete(root, true);
            }
            catch (Exception error) { failure = (failure ?? "") + " Cleanup: " + error.Message; }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "volume-result.json"), JsonSerializer.Serialize(new {
            success = failure is null, revision = Environment.GetEnvironmentVariable("GITHUB_SHA"), checks, error = failure }));
        return failure is null ? 0 : 1;
    }

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonVolumeUser(string user, string domain, string password, int type, int provider, out SafeAccessTokenHandle token);
}
