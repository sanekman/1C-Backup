using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using ICSharpCode.SharpZipLib.Zip;
using SPBackup.Core;

internal static partial class Program
{
    private sealed record UserStartupCheckpoint(Guid BootId, string OwnerSid, string Desktop, string DesktopHash,
        string Agent, string AgentHash, string Profile, Guid JobId, string JobsJson, string Revision);
    private static string DefaultOwnerProfile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneCBackup");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static async Task<int> PrepareUserStartupAsync(string desktop, string output)
    {
        Directory.CreateDirectory(output);
        Require(WindowsAgentService.QueryInfo() is null && !Directory.Exists(DefaultOwnerProfile), "User-startup fixture requires a fresh owner profile and no installed service.");
        desktop = Path.GetFullPath(desktop);
        var agent = Path.Combine(Path.GetDirectoryName(desktop)!, "SPBackup.Agent.exe");
        Require(File.Exists(agent) && File.Exists(desktop), "The real desktop and agent must be bundled together.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        Require(key.GetValue("OneCBackupAgent") is null, "Refusing to replace an existing startup entry.");
        var source = Path.Combine(output, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "Interactive startup VM fixture");
        Directory.CreateDirectory(DefaultOwnerProfile);
        var job = new BackupJob { Name = "Interactive user startup", Source = source, OneCMethod = OneCBackupMethod.FileArchive,
            DestinationDirectory = Path.Combine(output, "copies"), Enabled = true, ScheduleKind = ScheduleKind.SystemStartup };
        await new JobStore(Path.Combine(DefaultOwnerProfile, "jobs.json")).SaveAsync([job]);
        var checkpoint = new UserStartupCheckpoint(SystemBootSession.ReadId(), WindowsIdentity.GetCurrent().User!.Value,
            desktop, await HashAsync(desktop), agent, await HashAsync(agent), DefaultOwnerProfile, job.Id,
            await File.ReadAllTextAsync(Path.Combine(DefaultOwnerProfile, "jobs.json")), Environment.GetEnvironmentVariable("GITHUB_SHA")!);
        await File.WriteAllTextAsync(Path.Combine(output, "user-startup-checkpoint.json"), JsonSerializer.Serialize(checkpoint));
        key.SetValue("OneCBackupAgent", new AgentAutostartCommands(Path.GetDirectoryName(desktop)!).Current, RegistryValueKind.String);
        return 0;
    }

    private static async Task<int> VerifyUserStartupAsync(string desktop, string output)
    {
        var saved = JsonSerializer.Deserialize<UserStartupCheckpoint>(await File.ReadAllTextAsync(Path.Combine(output, "user-startup-checkpoint.json")))!;
        string? failure = null;
        var checks = new List<string>();
        try
        {
            Require(saved.BootId != SystemBootSession.ReadId() && saved.OwnerSid == WindowsIdentity.GetCurrent().User?.Value,
                "Interactive startup requires a real new boot and the same owner.");
            Require(WindowsAgentService.QueryInfo() is null && saved.Desktop == Path.GetFullPath(desktop)
                && saved.DesktopHash == await HashAsync(desktop) && saved.AgentHash == await HashAsync(saved.Agent),
                "The startup binaries changed or an unexpected service was installed.");
            var control = new LocalAgentControlStore(saved.Profile); LocalAgentState? state = null;
            await Wait(async () => (state = await control.ReadAsync()) is { Phase: LocalAgentPhase.Running, CanRunManually: true, RunsSchedules: true },
                "The registry startup command did not start the real background agent after user login.");
            using var process = Process.GetProcessById(state!.ProcessId);
            Require(process.SessionId > 0 && string.Equals(process.MainModule?.FileName, saved.Agent, StringComparison.OrdinalIgnoreCase),
                "The background agent did not start in an interactive user session from the bundled executable.");
            if (!OpenStartupProcessToken(process.Handle, 8, out var token)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                Require(identity.User?.Value == saved.OwnerSid, "The user-startup agent runs under another account.");
            checks.Add("real-boot-and-explorer-run-key-start-desktop-and-background-agent-under-owner");
            var history = new BackupRunHistory(Path.Combine(saved.Profile, "runs.json"));
            await Wait(async () => (await history.ReadAsync()).Any(run => run.JobId == saved.JobId && run.Status == BackupRunStatus.Succeeded), "User-startup backup did not finish.");
            var runs = await history.ReadAsync(); Require(runs.Count == 1, "The user-startup job ran more than once.");
            var catalog = new BackupCatalog(Path.Combine(saved.Profile, "copies.json"));
            var copy = (await catalog.ReadAsync()).Single();
            using (var zip = new ZipFile(copy.Backup.Location))
            {
                Require(zip.Count == 1, "User-startup archive contents differ.");
                using var reader = new StreamReader(zip.GetInputStream(0));
                Require(await reader.ReadToEndAsync() == "Interactive startup VM fixture", "User-startup archive did not restore expected bytes.");
            }
            Require(await File.ReadAllTextAsync(Path.Combine(saved.Profile, "jobs.json")) == saved.JobsJson, "User startup modified the saved job.");
            checks.Add("user-startup-backup-restores-real-bytes-once-and-keeps-saved-plan");
            await control.RequestStopAsync(state.SessionId);
            await WaitProcessExit(state.ProcessId, saved.Agent);
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        finally
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (new AgentAutostartCommands(Path.GetDirectoryName(saved.Desktop)!).Owns(key?.GetValue("OneCBackupAgent") as string))
                key!.DeleteValue("OneCBackupAgent", false);
        }
        var result = new { success = failure is null, revision = saved.Revision, previousBootId = saved.BootId,
            bootId = SystemBootSession.ReadId(), checks, error = failure };
        await File.WriteAllTextAsync(Path.Combine(output, "user-startup-result.json"), JsonSerializer.Serialize(result));
        return failure is null ? 0 : 1;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    private static extern bool OpenStartupProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
}
