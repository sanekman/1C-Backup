using System.Diagnostics;
using System.Text.Json;
using SPBackup.Core;

if (args.Length != 3 || !OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidOperationException("Expected the isolated Windows upgrade fixture.");
var profile = Path.GetFullPath(args[1]);
var result = Path.GetFullPath(args[2]);
var credentials = new WindowsSqlServerCredentialStore();
if (args[0] == "cleanup")
{
    if (File.Exists(result)) credentials.Delete(File.ReadAllText(result));
    return;
}
if (File.Exists(Path.Combine(profile, "external-monitors.json")) || !args[0].StartsWith("127.0.0.1,", StringComparison.Ordinal) ||
    FileVersionInfo.GetVersionInfo(typeof(ExternalBackupMonitor).Assembly.Location).ProductVersion?.StartsWith("0.2.0-alpha.40+", StringComparison.Ordinal) != true)
    throw new InvalidOperationException("Expected an unused profile and the published alpha.40 serializer.");

Directory.CreateDirectory(profile);
var settings = new AgentSettings { AgentId = Guid.NewGuid() };
await new AgentSettingsStore(Path.Combine(profile, "agent.json")).SaveAsync(settings);
var credential = "observation-upgrade-" + Guid.NewGuid().ToString("N");
await File.WriteAllTextAsync(result, credential);
credentials.Save(credential, "fixture-account", Guid.NewGuid().ToString("N"));
var source = new ExternalBackupMonitor
{
    Name = "SQL upgrade fixture", Kind = ExternalBackupSourceKind.SqlServerHistory, SqlServer = args[0],
    SqlAuthentication = SqlServerAuthentication.SqlServer, SqlCredentialKey = credential,
    Enabled = false
};
await new ExternalBackupMonitorStore(Path.Combine(profile, "external-monitors.json")).SaveAsync([source]);
var finished = DateTime.Now;
var item = new ExternalBackupObservation("old-report", args[0], "fixture", "D", finished.AddMinutes(-1), finished,
    "UTC", ExternalBackupOutcome.Completed, 128, [@"D:\Fixtures\old.bak"], "SqlServerMsdbHistory");
await new ExternalBackupOutbox(Path.Combine(profile, "external-backups.json")).ObserveAsync(settings, source, [item]);
