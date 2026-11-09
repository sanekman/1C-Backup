using System.Text.Json;
using Microsoft.Data.SqlClient;
using SPBackup.Core;

if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SPBACKUP_NATIVE_SQL_ENGINE_TEST") != "1")
    throw new InvalidOperationException("Run only on the owned Windows SQL engine stand.");
var root = Path.GetFullPath(args.Single());
var server = Environment.GetEnvironmentVariable("SPBACKUP_SQL_TEST_SERVER") ?? throw new InvalidOperationException("Missing owned server.");
var password = Environment.GetEnvironmentVariable("SPBACKUP_SQL_TEST_PASSWORD") ?? throw new InvalidOperationException("Missing owned login.");
var prefix = "SPB_" + Guid.NewGuid().ToString("N");
var database = prefix + "_source"; var login = prefix + "_login";
var key = Guid.NewGuid().ToString("N"); var wrongKey = Guid.NewGuid().ToString("N");
var credentials = new WindowsSqlServerCredentialStore();
var checks = new List<string>(); string? failure = null; string? engine = null;
var restored = new List<string>();
string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
SqlConnection Owner(string name = "master") => new(new SqlConnectionStringBuilder
{
    DataSource = server, InitialCatalog = name, IntegratedSecurity = true,
    Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = false,
    Pooling = false, ConnectTimeout = 15
}.ConnectionString);
async Task<object?> Execute(string sql, string name = "master")
{
    await using var connection = Owner(name); await connection.OpenAsync();
    await using var command = connection.CreateCommand(); command.CommandTimeout = 90; command.CommandText = sql;
    return await command.ExecuteScalarAsync();
}
try
{
    Require(Directory.Exists(root) && File.Exists(Path.Combine(root, "owned-stand.marker")), "Missing owned stand marker.");
    Require(Convert.ToInt32(await Execute("SELECT CONVERT(int,SERVERPROPERTY('IsLocalDB'))")) == 0, "LocalDB cannot satisfy the full-engine stand.");
    Require(Convert.ToInt32(await Execute("SELECT CONVERT(int,SERVERPROPERTY('IsIntegratedSecurityOnly'))")) == 0, "The stand must use mixed authentication.");
    engine = Convert.ToString(await Execute("SELECT CONVERT(nvarchar(256),SERVERPROPERTY('ProductVersion'))+' '+CONVERT(nvarchar(256),SERVERPROPERTY('Edition'))"));
    checks.Add("full-windows-engine-mixed-mode-with-verified-tls-not-localdb");
    await Execute($"CREATE DATABASE [{database}]; ALTER DATABASE [{database}] SET RECOVERY FULL;");
    await Execute("CREATE TABLE dbo.Fixture(Id int PRIMARY KEY, Value nvarchar(100)); INSERT dbo.Fixture VALUES(1,N'Проверка копии'),(2,N'fixture two');", database);
    await Execute($"CREATE LOGIN [{login}] WITH PASSWORD={Literal(password)}, CHECK_POLICY=OFF;");
    // VERIFYONLY needs create-database permission in addition to BACKUP DATABASE.
    // This is an owned functional stand, not a least-privilege deployment claim.
    await Execute($"ALTER SERVER ROLE dbcreator ADD MEMBER [{login}];");
    await Execute($"CREATE USER [{login}] FOR LOGIN [{login}]; ALTER ROLE db_backupoperator ADD MEMBER [{login}];", database);
    credentials.Save(key, login, password); credentials.Save(wrongKey, login, "Deliberately wrong isolated SQL test password");
    var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
    var job = new BackupJob { Name = "Full SQL engine fixture", Kind = DatabaseKind.SqlServer, Source = database,
        Enabled = false, ScheduleKind = ScheduleKind.Manual, KeepLast = 10,
        DestinationDirectory = Path.Combine(root, "copies"), TemporaryDirectory = Path.Combine(root, "work"),
        SqlServer = new() { Server = server, ServerBackupDirectory = stage, AgentReadDirectory = stage, TimeoutMinutes = 2 } };
    var checker = new SourceReadinessChecker(new BackupProcessRunner(), Path.Combine(root, "probe-work"));
    var windowsProbe = await checker.CheckAsync(job); Require(windowsProbe.Succeeded, "Windows login source check failed: " + windowsProbe.Details);
    var sqlJob = job with { Id = Guid.NewGuid(), SqlServer = job.SqlServer with { Authentication = SqlServerAuthentication.SqlServer, CredentialKey = key } };
    var sqlProbe = await checker.CheckAsync(sqlJob); Require(sqlProbe.Succeeded, "SQL login source check failed: " + sqlProbe.Details);
    var wrong = await checker.CheckAsync(sqlJob with { SqlServer = sqlJob.SqlServer! with { CredentialKey = wrongKey } });
    Require(!wrong.Succeeded && !wrong.Cancelled, "Wrong SQL password was accepted.");
    // The stand certificate names the computer only, never its loopback IP.
    var mismatch = job with { SqlServer = job.SqlServer with { Server = "127.0.0.1," + server.Split(',').Last() } };
    Require(!(await checker.CheckAsync(mismatch)).Succeeded, "A mismatched server certificate was accepted by default.");
    Require((await checker.CheckAsync(mismatch with { SqlServer = mismatch.SqlServer! with { TrustServerCertificate = true } })).Succeeded,
        "Explicit untrusted-certificate opt-in did not connect.");
    Require((await checker.CheckAsync(sqlJob)).Succeeded, "A rejected login poisoned a healthy connection.");
    Require(!Directory.EnumerateFiles(stage).Any(), "Read-only source checks created backup files.");
    checks.Add("windows-and-vault-sql-logins-wrong-password-and-hostname-rejection-explicit-opt-in-and-recovery");
    var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
    var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
    var storage = new FileBackupStorage();
    var runner = new LocalBackupRunner(new SqlServerBackupExecutor(credentials: credentials), new BackupCompletion(storage, catalog), history, Path.Combine(root, "unused"));
    foreach (var current in new[] { job, sqlJob })
    {
        var run = await runner.RunAsync(current); Require(run.Status == BackupRunStatus.Succeeded, "Full-engine backup failed: " + run.Error);
        var record = (await catalog.ReadAsync()).Single(item => item.JobId == current.Id);
        await new BackupCopyVerifier(storage).VerifyAsync(record, current);
        var target = prefix + "_restored_" + restored.Count; restored.Add(target);
        await Execute($"RESTORE VERIFYONLY FROM DISK={Literal(record.Backup.Location)} WITH CHECKSUM;");
        // SQL generates the logical names from the uniquely owned database name.
        await Execute($"RESTORE DATABASE [{target}] FROM DISK={Literal(record.Backup.Location)} WITH CHECKSUM, MOVE {Literal(database)} TO {Literal(Path.Combine(root, target + ".mdf"))}, MOVE {Literal(database + "_log")} TO {Literal(Path.Combine(root, target + ".ldf"))};");
        Require(Convert.ToInt32(await Execute("SELECT COUNT(*) FROM dbo.Fixture WHERE (Id=1 AND Value=N'Проверка копии') OR (Id=2 AND Value=N'fixture two')", target)) == 2,
            "Restored SQL data differs.");
    }
    checks.Add("windows-and-sql-auth-backups-delivered-sha256-verifyonly-checksum-and-restored-unicode-data");
    var beforeCopies = JsonSerializer.Serialize(await catalog.ReadAsync());
    var external = Path.Combine(root, "external"); Directory.CreateDirectory(external);
    await Execute($"BACKUP DATABASE [{database}] TO DISK={Literal(Path.Combine(external, "full.bak"))} WITH CHECKSUM;");
    await Execute("INSERT dbo.Fixture VALUES(3,N'external history');", database);
    await Execute($"BACKUP DATABASE [{database}] TO DISK={Literal(Path.Combine(external, "diff.bak"))} WITH DIFFERENTIAL,CHECKSUM;");
    await Execute($"BACKUP LOG [{database}] TO DISK={Literal(Path.Combine(external, "log.trn"))} WITH CHECKSUM;");
    var monitor = new ExternalBackupMonitor { Name = "Owned SQL history", Kind = ExternalBackupSourceKind.SqlServerHistory,
        SqlServer = server, DatabaseName = database, TimeZoneId = TimeZoneInfo.Local.Id };
    var source = new SqlServerBackupHistorySource(credentials: credentials);
    var observed = await source.ReadAsync(monitor);
    Require(new[] { "D", "I", "L" }.All(type => observed.Any(item => item.BackupType == type && item.SizeBytes > 0 && item.Outcome == ExternalBackupOutcome.Completed)),
        "Real full/differential/log history was not observed.");
    Require((await source.ReadAsync(monitor with { DatabaseName = prefix + "_empty" })).Count == 0, "A new source reused previous database history.");
    var denied = monitor with { SqlAuthentication = SqlServerAuthentication.SqlServer, SqlCredentialKey = key };
    await Execute($"CREATE USER [{login}] FOR LOGIN [{login}]; DENY SELECT ON dbo.backupset TO [{login}]; DENY SELECT ON dbo.backupmediafamily TO [{login}];", "msdb");
    var deniedCorrectly = false;
    try { await source.ReadAsync(denied); } catch (Exception error) when (error is SqlException or IOException) { deniedCorrectly = true; }
    Require(deniedCorrectly, "A SQL user without msdb read permissions was reported as a successful poll.");
    Require((await source.ReadAsync(monitor)).Count == observed.Count, "Failed monitoring affected the next healthy read.");
    Require(beforeCopies == JsonSerializer.Serialize(await catalog.ReadAsync()), "Monitoring changed the agent backup catalog.");
    checks.Add("real-msdb-full-differential-log-empty-source-and-permission-error-without-owning-external-copies");
}
catch (Exception error) { failure = (error.GetType().Name + ": " + error.Message).Replace(password, "[redacted]"); }
finally
{
    credentials.Delete(key); credentials.Delete(wrongKey);
    try
    {
        foreach (var name in restored.Append(database))
            await Execute($"IF DB_ID({Literal(name)}) IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END;");
        await Execute($"IF USER_ID({Literal(login)}) IS NOT NULL DROP USER [{login}];", "msdb");
        await Execute($"IF SUSER_ID({Literal(login)}) IS NOT NULL DROP LOGIN [{login}];");
    }
    catch (Exception error) { failure = (failure ?? "") + " Cleanup: " + error.GetType().Name; }
    var result = new { success = failure is null, source = Environment.GetEnvironmentVariable("GITHUB_SHA"),
        scope = "full-engine-local-tcp-current-user", remoteHostVerified = false, serviceIdentityVerified = false,
        engine, checks, error = failure };
    Directory.CreateDirectory(Path.Combine(root, "report"));
    await File.WriteAllTextAsync(Path.Combine(root, "report", "sql-engine-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
}
return failure is null ? 0 : 1;
