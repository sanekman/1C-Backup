using System.Text.Json;
using Microsoft.Data.SqlClient;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class NativeSqlServerIntegrationTests
{
    [NativeSqlFact]
    [Trait("Category", "NativeSqlIntegration")]
    public async Task LocalDbBackupRestoresDataAndHistoryReportsNativeBackupsWithoutOwningThem()
    {
        var executable = Environment.GetEnvironmentVariable("ONECBACKUP_TEST_LOCALDB_EXE")
            ?? throw new InvalidOperationException("Native SQL CI must provide SqlLocalDB.exe.");
        Assert.True(File.Exists(executable), "SqlLocalDB.exe is unavailable.");
        var instance = "OneCBackupTest_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), instance);
        var server = "(localdb)\\" + instance;
        var sourceName = "SourceFixture";
        var restoredName = "RestoredFixture";
        Directory.CreateDirectory(root);
        var created = false;
        SqlConnection Connection(string database = "master") => new(new SqlConnectionStringBuilder
        {
            DataSource = server, InitialCatalog = database, IntegratedSecurity = true, Pooling = false,
            // A fresh per-user LocalDB uses a local named pipe and Windows authentication.
            Encrypt = SqlConnectionEncryptOption.Optional, TrustServerCertificate = false, ConnectTimeout = 30
        }.ConnectionString);
        async Task Execute(string sql, string database = "master")
        {
            await using var connection = Connection(database); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync();
        }
        string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
        try
        {
            await NativeSqlTestTools.RunAsync(executable, ["create", instance, "-s"]); created = true;
            await Execute($"CREATE DATABASE [{sourceName}] ON PRIMARY (NAME=N'FixtureData', FILENAME={Literal(Path.Combine(root, "source.mdf"))}) LOG ON (NAME=N'FixtureLog', FILENAME={Literal(Path.Combine(root, "source.ldf"))});");
            await Execute("CREATE TABLE dbo.BackupFixture (Id int PRIMARY KEY, Value nvarchar(100) NOT NULL); INSERT dbo.BackupFixture VALUES (1,N'Резервная копия'),(2,N'fixture two');", sourceName);
            var stage = Path.Combine(root, "server-stage"); Directory.CreateDirectory(stage);
            var catalog = new BackupCatalog(Path.Combine(root, "copies.json"));
            var history = new BackupRunHistory(Path.Combine(root, "runs.json"));
            var storage = new FileBackupStorage();
            var job = new BackupJob { Name = "Native SQL", Kind = DatabaseKind.SqlServer, Source = sourceName,
                DestinationDirectory = Path.Combine(root, "copies"), TemporaryDirectory = Path.Combine(root, "work"),
                KeepLast = 1, ScheduleKind = ScheduleKind.Manual, Enabled = false,
                SqlServer = new() { Server = server, ServerBackupDirectory = stage, AgentReadDirectory = stage, TimeoutMinutes = 2 } };
            var runner = new LocalBackupRunner(new SqlServerBackupExecutor(target =>
            {
                Assert.Equal(server, target.SqlServer!.Server); return Connection();
            }), new BackupCompletion(storage, catalog), history, Path.Combine(root, "unused-work"));
            var run = await runner.RunAsync(job);
            Assert.True(run.Status == BackupRunStatus.Succeeded, run.Error);
            Assert.False(Directory.Exists(run.WorkDirectory));
            var record = Assert.Single(await catalog.ReadAsync());
            await new BackupCopyVerifier(storage).VerifyAsync(record, job);
            await Execute($"RESTORE DATABASE [{restoredName}] FROM DISK={Literal(record.Backup.Location)} WITH MOVE N'FixtureData' TO {Literal(Path.Combine(root, "restored.mdf"))}, MOVE N'FixtureLog' TO {Literal(Path.Combine(root, "restored.ldf"))}, CHECKSUM;");
            await using (var connection = Connection(restoredName))
            {
                await connection.OpenAsync(); await using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Value FROM dbo.BackupFixture ORDER BY Id;";
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt32(0)); Assert.Equal("Резервная копия", reader.GetString(1));
                Assert.True(await reader.ReadAsync()); Assert.Equal(2, reader.GetInt32(0)); Assert.Equal("fixture two", reader.GetString(1));
                Assert.False(await reader.ReadAsync());
            }
            var before = JsonSerializer.Serialize(await catalog.ReadAsync());
            var failed = await runner.RunAsync(job with { SqlServer = job.SqlServer with { AgentReadDirectory = Path.Combine(root, "wrong-agent-path") } });
            Assert.Equal(BackupRunStatus.Failed, failed.Status);
            Assert.Contains("не может прочитать", failed.Error);
            Assert.True(Directory.Exists(failed.WorkDirectory));
            Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
            Assert.True(File.Exists(record.Backup.Location));
            var monitor = new ExternalBackupMonitor { Name = "Native SQL history", SqlServer = server,
                DatabaseName = sourceName, TimeZoneId = TimeZoneInfo.Local.Id, Kind = ExternalBackupSourceKind.SqlServerHistory };
            var observations = await new SqlServerBackupHistorySource(target =>
            { Assert.Equal(server, target.SqlServer); return Connection(); }).ReadAsync(monitor);
            Assert.Equal(2, observations.Count);
            Assert.All(observations, observation =>
            {
                Assert.Equal(ExternalBackupOutcome.Completed, observation.Outcome);
                Assert.Equal("SqlServerMsdbHistory", observation.Evidence);
                Assert.True(observation.SizeBytes > 0);
                Assert.All(observation.Locations, location => Assert.True(File.Exists(location)));
            });
            var outbox = new ExternalBackupOutbox(Path.Combine(root, "external.json")); var settings = new AgentSettings();
            Assert.Equal(2, await outbox.ObserveAsync(settings, monitor, observations));
            Assert.Equal(0, await outbox.ObserveAsync(settings, monitor, observations));
            Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
            Assert.Equal(2, (await history.ReadAsync()).Count);
        }
        finally
        {
            if (created)
            {
                await NativeSqlTestTools.RunAsync(executable, ["stop", instance, "-k"]);
                await NativeSqlTestTools.RunAsync(executable, ["delete", instance]);
            }
            Directory.Delete(root, true);
        }
    }
}
