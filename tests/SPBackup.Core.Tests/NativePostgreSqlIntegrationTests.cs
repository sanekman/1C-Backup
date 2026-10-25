using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class NativePostgreSqlIntegrationTests
{
    private sealed class FixturePassword(string value) : IArchivePasswordStore
    {
        public Task<string> GetAsync(string key, CancellationToken cancellationToken = default)
        { Assert.Equal("fixture-archive", key); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(value); }
    }

    [NativeSqlFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [Trait("Category", "NativeSqlIntegration")]
    public async Task EncryptedPgDumpReachesTwoStoresWithSeparateRetentionRestoresDataAndEmitsExternalReport()
    {
        var bin = Environment.GetEnvironmentVariable("PGBIN")
            ?? throw new InvalidOperationException("Native SQL CI must provide the PostgreSQL bin directory.");
        if (OperatingSystem.IsWindows())
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bin));
            var standard = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(Environment.GetFolderPath).Where(path => !string.IsNullOrEmpty(path))
                .Any(path => normalized.StartsWith(Path.Combine(path, "PostgreSQL") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var tools = standard ? PostgreSqlToolDiscovery.FindInstalledOnWindows() : PostgreSqlToolDiscovery.Find([bin]);
            Assert.Contains(tools, tool => string.Equals(tool.Directory, normalized, StringComparison.OrdinalIgnoreCase));
        }
        string Tool(string name)
        {
            var path = Path.Combine(bin, name + ".exe"); Assert.True(File.Exists(path), "Missing PostgreSQL fixture tool: " + name); return path;
        }
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-pg-" + Guid.NewGuid().ToString("N"));
        var cluster = Path.Combine(root, "cluster"); var passwordFile = Path.Combine(root, "init-password");
        Directory.CreateDirectory(root);
        var password = "native:password\\" + Guid.NewGuid().ToString("N");
        var credentialKey = Guid.NewGuid().ToString("N");
        var credentials = new WindowsPostgreSqlCredentialStore();
        var archivePassword = Guid.NewGuid().ToString("N");
        var user = "onec_" + Guid.NewGuid().ToString("N")[..12];
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await File.WriteAllTextAsync(passwordFile, password);
        var pgpass = Path.Combine(root, "pgpass.conf");
        await File.WriteAllTextAsync(pgpass, $"127.0.0.1:{port}:*:{user}:" + password.Replace("\\", "\\\\").Replace(":", "\\:") + "\n");
        var environment = new Dictionary<string, string?>
        {
            ["PGHOST"] = "127.0.0.1", ["PGPORT"] = port.ToString(), ["PGUSER"] = user,
            ["PGPASSFILE"] = pgpass, ["PGPASSWORD"] = null, ["PGSERVICE"] = null, ["PGSERVICEFILE"] = null,
            ["PGHOSTADDR"] = null,
            ["PGCLIENTENCODING"] = "UTF8"
        };
        var previous = environment.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var initialized = false;
        async Task<string> Sql(string database, string sql) => await NativeSqlTestTools.RunAsync(Tool("psql"),
            ["--no-password", "--host=127.0.0.1", "--port=" + port, "--username=" + user,
                "--dbname=" + database, "--set=ON_ERROR_STOP=1", "--tuples-only", "--no-align", "--command=" + sql], environment);
        try
        {
            await NativeSqlTestTools.RunAsync(Tool("initdb"), ["--pgdata=" + cluster, "--username=" + user,
                "--auth=scram-sha-256", "--pwfile=" + passwordFile, "--encoding=UTF8", "--locale=C"]);
            initialized = true;
            File.Delete(passwordFile);
            // The background server can inherit redirected pipe handles on Windows.
            // pg_ctl reports readiness itself; server diagnostics go to its private log.
            await NativeSqlTestTools.RunAsync(Tool("pg_ctl"), ["start", "-D", cluster, "-w", "-t", "60",
                "-l", Path.Combine(root, "postgres.log"), "-o", $"-h 127.0.0.1 -p {port}"], captureOutput: false);
            foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            credentials.Save(credentialKey, user, password);
            // The agent must use the saved secret, not inherited authentication or a different target address.
            Environment.SetEnvironmentVariable("PGPASSFILE", Path.Combine(root, "missing-ambient-pgpass"));
            Environment.SetEnvironmentVariable("PGPASSWORD", "incorrect ambient password");
            Environment.SetEnvironmentVariable("PGSERVICE", "missing-ambient-service");
            Environment.SetEnvironmentVariable("PGHOSTADDR", "192.0.2.1");
            await Sql("postgres", "CREATE DATABASE source_fixture;");
            await Sql("source_fixture", "CREATE TABLE backup_fixture (id integer PRIMARY KEY, value text NOT NULL); INSERT INTO backup_fixture VALUES (1,'first'),(2,'second');");
            var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
            var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
            var storage = new FileBackupStorage(); var passwordStore = new FixturePassword(archivePassword);
            var job = new BackupJob { Name = "Native PostgreSQL", Kind = DatabaseKind.PostgreSql, Source = "source_fixture",
                DestinationDirectory = Path.Combine(root, "primary"), TemporaryDirectory = Path.Combine(root, "work"), KeepLast = 2,
                AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "secondary"), KeepLast = 1 }],
                ScheduleKind = ScheduleKind.Manual, Enabled = false, ArchiveCopies = true, ArchivePasswordKey = "fixture-archive",
                PostgreSql = new() { Host = "127.0.0.1", Port = port, Username = user, CredentialKey = credentialKey,
                    PgDumpPath = Tool("pg_dump"), PgRestorePath = Tool("pg_restore"), TimeoutMinutes = 2 } };
            var sourceChecker = new SourceReadinessChecker(new BackupProcessRunner(), Path.Combine(root, "unused-work"));
            var ready = await sourceChecker.CheckAsync(job); Assert.True(ready.Succeeded, ready.Details);
            credentials.Save(credentialKey, user, "incorrect saved password");
            Assert.False((await sourceChecker.CheckAsync(job)).Succeeded);
            credentials.Save(credentialKey, user, password);
            Assert.False((await sourceChecker.CheckAsync(job with { Source = "missing_fixture_database" })).Succeeded);
            Assert.Empty(await history.ReadAsync()); Assert.Empty(await catalog.ReadAsync());
            Assert.Empty(Directory.GetFileSystemEntries(job.TemporaryDirectory!));
            var runner = new LocalBackupRunner(new PostgreSqlBackupExecutor(new BackupProcessRunner()),
                new BackupCompletion(storage, catalog, passwordStore), history, Path.Combine(root, "unused-work"));
            var first = await runner.RunAsync(job); Assert.True(first.Status == BackupRunStatus.Succeeded, first.Error);
            Assert.False(Directory.Exists(first.WorkDirectory));
            await Sql("source_fixture", "INSERT INTO backup_fixture VALUES (3,'third');");
            var second = await runner.RunAsync(job); Assert.True(second.Status == BackupRunStatus.Succeeded, second.Error);
            Assert.False(Directory.Exists(second.WorkDirectory));
            var records = await catalog.ReadAsync(); Assert.Equal(3, records.Count);
            var latest = second.Destinations!.Select(result => result.Record!).ToArray(); Assert.Equal(2, latest.Length);
            Assert.All(latest, record => Assert.Equal("fixture-archive", record.Backup.ArchivePasswordKey));
            Assert.Equal(latest[0].Backup.Sha256, latest[1].Backup.Sha256);
            Assert.Equal(2, Directory.GetFiles(job.DestinationDirectory, "*.zip").Length);
            Assert.Single(Directory.GetFiles(job.AdditionalDestinations![0].DestinationDirectory, "*.zip"));
            var audit = await new LatestBackupAudit(storage, catalog).CheckAsync(job);
            Assert.All(audit.Destinations, result => Assert.Equal(BackupAuditStatus.Verified, result.Status));
            var downloaded = Path.Combine(root, "downloaded.zip");
            await new BackupCopyExporter(storage).ExportAsync(latest[1], job, downloaded);
            var dump = Path.Combine(root, "restored-input.dump");
            using (var zip = new ZipFile(downloaded) { Password = archivePassword })
            {
                Assert.Equal(1, zip.Count); Assert.True(zip[0].IsCrypted); Assert.Equal(256, zip[0].AESKeySize);
                await using var input = zip.GetInputStream(0);
                await using var output = new FileStream(dump, FileMode.CreateNew, FileAccess.Write);
                await input.CopyToAsync(output);
            }
            await Sql("postgres", "CREATE DATABASE restored_fixture;");
            using (var connection = await PostgreSqlConnectionEnvironment.OpenAsync(job with { Source = "restored_fixture" }, credentials, CancellationToken.None))
                await NativeSqlTestTools.RunAsync(Tool("pg_restore"), ["--no-password", "--exit-on-error", "--host=127.0.0.1",
                    "--port=" + port, "--username=" + user, "--dbname=restored_fixture", dump], connection.Environment);
            Assert.Equal("1:first,2:second,3:third", await Sql("restored_fixture", "SELECT string_agg(id::text || ':' || value, ',' ORDER BY id) FROM backup_fixture;"));
            var repository = Environment.GetEnvironmentVariable("ONECBACKUP_TEST_REPOSITORY") ?? Environment.CurrentDirectory;
            var script = Path.Combine(repository, "tools", "postgresql_backup_report.py"); Assert.True(File.Exists(script));
            var reports = Path.Combine(root, "external-reports");
            await NativeSqlTestTools.RunAsync("python", [script, "--database", job.Source, "--instance", "native-fixture",
                "--backup-directory", Path.Combine(root, "external-backups"), "--report-directory", reports,
                "--pg-dump", Tool("pg_dump"), "--pg-restore", Tool("pg_restore"), "--timeout-seconds", "60"], environment);
            var before = JsonSerializer.Serialize(await catalog.ReadAsync());
            var monitor = new ExternalBackupMonitor { Name = "Native PG report", Kind = ExternalBackupSourceKind.PostgreSqlReports,
                ReportDirectory = reports, DatabaseName = job.Source };
            var observation = Assert.Single(await new PostgreSqlBackupReportSource().ReadAsync(monitor));
            Assert.Equal(ExternalBackupOutcome.Completed, observation.Outcome);
            Assert.Equal("PgRestoreCatalogChecked", observation.Evidence);
            Assert.True(File.Exists(Assert.Single(observation.Locations)));
            var outbox = new ExternalBackupOutbox(Path.Combine(root, "external.json")); var settings = new AgentSettings();
            Assert.Equal(1, await outbox.ObserveAsync(settings, monitor, [observation]));
            Assert.Equal(0, await outbox.ObserveAsync(settings, monitor, [observation]));
            Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
            Assert.Equal(2, (await history.ReadAsync()).Count);
        }
        finally
        {
            credentials.Delete(credentialKey);
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            if (initialized && File.Exists(Path.Combine(cluster, "postmaster.pid")))
                await NativeSqlTestTools.RunAsync(Tool("pg_ctl"), ["stop", "-D", cluster, "-w", "-t", "60", "-m", "fast"]);
            Directory.Delete(root, true);
        }
    }
}
