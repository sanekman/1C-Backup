using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPBackup.Core;

var output = Path.GetFullPath(args.Single()); Directory.CreateDirectory(output);
var source = Environment.GetEnvironmentVariable("SPBACKUP_COMPAT_SOURCE") ?? throw new InvalidOperationException("Exact source is required.");
var manifest = JsonSerializer.Deserialize<Toolchain[]>(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("SPBACKUP_COMPAT_TOOLS")!))!;
var cases = new List<Case>(); var versions = new List<object>();
var fixtureSecrets = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "SPBackup-pg-compat-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var phase = "validate-toolchains"; var succeeded = false; string? error = null;
string? detail = null;
var previousBin = Environment.GetEnvironmentVariable("PGBIN");
try
{
    Require(OperatingSystem.IsWindows(), "windows-required");
    Require(manifest.Select(t => t.Major).Order().SequenceEqual(new[] { 15, 16, 17, 18 }), "four-distinct-majors-required");
    foreach (var tool in manifest)
    {
        var dump = await Version(Path.Combine(tool.Bin, "pg_dump.exe"));
        var restore = await Version(Path.Combine(tool.Bin, "pg_restore.exe"));
        Require(Major(dump) == tool.Major && Major(restore) == tool.Major, "actual-tools-major-mismatch");
        versions.Add(new { tool.Major, dump, restore, tool.Url, tool.Sha256 });
    }
    var newest = manifest.Single(t => t.Major == 18);
    Environment.SetEnvironmentVariable("PGBIN", newest.Bin);
    await using var upgradeTarget = new PostgreSqlFixture(); fixtureSecrets.Add(upgradeTarget.Password);
    phase = "upgrade-target-start"; await upgradeTarget.StartAsync();
    foreach (var server in manifest.OrderBy(t => t.Major))
    {
        phase = "server-" + server.Major + "-start";
        Environment.SetEnvironmentVariable("PGBIN", server.Bin);
        await using var fixture = new PostgreSqlFixture(); fixtureSecrets.Add(fixture.Password); await fixture.StartAsync();
        phase = "server-" + server.Major + "-seed";
        var version = await fixture.PrepareCompatibilityDataAsync();
        Require(int.Parse(version.Split('.')[0]) == server.Major, "actual-server-major-mismatch");
        var credentials = new WindowsPostgreSqlCredentialStore(); var key = Guid.NewGuid().ToString("N");
        credentials.Save(key, fixture.Username, fixture.Password);
        try
        {
            var work = Path.Combine(root, "server-" + server.Major); Directory.CreateDirectory(work);
            var catalog = new BackupCatalog(Path.Combine(work, "copies.json"));
            var history = new BackupRunHistory(Path.Combine(work, "runs.json"));
            var storage = new FileBackupStorage();
            var runner = new LocalBackupRunner(new PostgreSqlBackupExecutor(new BackupProcessRunner(), credentials),
                new BackupCompletion(storage, catalog), history, Path.Combine(work, "work"));
            var original = fixture.Job(Path.Combine(work, "copies"), key, "unused") with
                { ArchiveCopies = false, ArchivePasswordKey = null, KeepLast = 20 };
            original = original with { PostgreSql = original.PostgreSql! with { Tls = new() { RootCertificatePath = fixture.RootCertificatePath } } };
            foreach (var client in manifest.OrderByDescending(t => t.Major))
                await Check(client, client, client.Major < server.Major ? "dump-rejected" : "restored");
            if (server.Major == 15)
            {
                await Check(manifest.Single(t => t.Major == 18), manifest.Single(t => t.Major == 15), "catalog-rejected");
                await Check(manifest.Single(t => t.Major == 18), manifest.Single(t => t.Major == 18), "recovered");
            }
            async Task Check(Toolchain dump, Toolchain restore, string expected)
            {
                phase = $"server-{server.Major}-dump-{dump.Major}-restore-{restore.Major}-{expected}";
                var before = await catalog.ReadAsync(); var beforeJson = JsonSerializer.Serialize(before);
                var hashes = before.ToDictionary(r => r.Id, r => Hash(r.Backup.Location));
                var job = original with { Id = Guid.NewGuid(), Name = phase, PostgreSql = original.PostgreSql! with
                    { PgDumpPath = Path.Combine(dump.Bin,"pg_dump.exe"), PgRestorePath = Path.Combine(restore.Bin,"pg_restore.exe") } };
                var run = await runner.RunAsync(job);
                var after = await catalog.ReadAsync();
                foreach (var copy in before) Require(Hash(copy.Backup.Location) == hashes[copy.Id], "existing-copy-changed");
                if (expected is "restored" or "recovered")
                {
                    Require(run.Status == BackupRunStatus.Succeeded && after.Count == before.Count + 1, "backup-not-successful");
                    var copy = after.Single(r => r.JobId == job.Id); await new BackupCopyVerifier(storage).VerifyAsync(copy,job);
                    // Newer pg_dump output is intended for the same or a newer target than that tool.
                    // Do not claim that SQL produced by v18 is guaranteed to load back into v15.
                    var target = dump.Major == server.Major ? fixture : upgradeTarget;
                    await target.AssertCompatibilityRestoresAsync(copy.Backup.Location,
                        dump.Major == server.Major ? job.PostgreSql.PgRestorePath : Path.Combine(newest.Bin,"pg_restore.exe"));
                    Require(!Directory.Exists(run.WorkDirectory), "successful-work-not-cleaned");
                }
                else
                {
                    Require(run.Status == BackupRunStatus.Failed && beforeJson == JsonSerializer.Serialize(after), "incompatible-copy-published");
                    var name = expected == "dump-rejected" ? "pg_dump" : "pg_restore";
                    Require(run.Error?.Contains(name, StringComparison.Ordinal) == true, "failing-tool-not-explained");
                    Require(File.Exists(Path.Combine(run.WorkDirectory, name + ".log")), "failure-log-missing");
                    var log = await File.ReadAllTextAsync(Path.Combine(run.WorkDirectory, name + ".log"));
                    Require(log.Contains(expected == "dump-rejected" ? "server version mismatch" : "unsupported version", StringComparison.OrdinalIgnoreCase),
                        "failure-was-not-version-incompatibility");
                    if (expected == "catalog-rejected") Require(Directory.EnumerateFiles(run.WorkDirectory,"*.dump").Any(), "dump-not-created-before-catalog-rejection");
                    Require(Directory.GetFiles(job.DestinationDirectory,"*.dump").Length == after.Count, "unregistered-copy-delivered");
                }
                cases.Add(new(server.Major, version, dump.Major, restore.Major,
                    expected is "restored" or "recovered" ? dump.Major == server.Major ? server.Major : 18 : null, expected, true));
            }
        }
        finally { credentials.Delete(key); }
    }
    Require(cases.Count == 18 && cases.Count(c => c.Expected == "restored") == 10
        && cases.Count(c => c.Expected == "dump-rejected") == 6, "matrix-incomplete");
    succeeded = true;
}
catch (Exception exception)
{
    error = exception is InvalidDataException ? exception.Message : exception.GetType().Name;
    if (exception is IOException)
    {
        detail = exception.Message;
        foreach (var secret in fixtureSecrets) detail = detail.Replace(secret,"[redacted]",StringComparison.Ordinal);
        detail = detail.Replace(Path.GetTempPath(),"[temporary]/",StringComparison.OrdinalIgnoreCase);
        if (detail.Length > 600) detail = detail[..600];
    }
}
finally
{
    Environment.SetEnvironmentVariable("PGBIN", previousBin);
    await File.WriteAllTextAsync(Path.Combine(output,"postgresql-compatibility-result.json"), JsonSerializer.Serialize(new
        { success = succeeded, source, scope = "windows-current-user-native-tools-vault-scram-verify-full", serviceIdentityVerified = false,
            phase, error, detail, toolchains = versions, cases }, new JsonSerializerOptions { WriteIndented = true }));
    if (Directory.Exists(root)) Directory.Delete(root,true);
}
return succeeded ? 0 : 1;

static void Require(bool value,string code) { if (!value) throw new InvalidDataException(code); }
static string Hash(string path) { using var input=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
static int Major(string version) => int.Parse(Regex.Match(version,@"PostgreSQL\) (\d+)\.").Groups[1].Value);
static async Task<string> Version(string executable)
{
    var info = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
    info.ArgumentList.Add("--version"); using var process=Process.Start(info) ?? throw new IOException("version-start-failed");
    var output=process.StandardOutput.ReadToEndAsync(); var errors=process.StandardError.ReadToEndAsync();
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
    var text=await output; await errors;
    Require(process.ExitCode == 0,"version-command-failed"); return text.Trim();
}
sealed record Toolchain(int Major,string Bin,string Url,string Sha256);
sealed record Case(int ServerMajor,string ServerVersion,int DumpMajor,int RestoreMajor,int? RestoredServerMajor,string Expected,bool Success);
