#nullable disable
using System.Data;
using System.Data.Common;
using System.IO.Compression;
using Microsoft.Data.SqlClient;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class DatabaseExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-db-execution-" + Guid.NewGuid());
    private readonly Guid jobId = Guid.NewGuid();
    private BackupJob SqlJob => new()
    {
        Id = jobId, Name = "SQL", Kind = DatabaseKind.SqlServer, Source = "db'; DROP DATABASE other; --",
        DestinationDirectory = Path.Combine(root, "copies"),
        SqlServer = new SqlServerBackupSettings { Server = "SQL", ServerBackupDirectory = Path.Combine(root, "sql-stage"),
            AgentReadDirectory = Path.Combine(root, "sql-stage") }
    };
    private BackupJob PgJob => new()
    {
        Id = jobId, Name = "PG", Kind = DatabaseKind.PostgreSql, Source = "database with spaces",
        DestinationDirectory = Path.Combine(root, "copies"),
        PostgreSql = new PostgreSqlBackupSettings { PgDumpPath = Path.Combine(root, "pg_dump.exe"),
            PgRestorePath = Path.Combine(root, "pg_restore.exe"), Host = "pg.example.com", Username = "backup-user" }
    };

    public DatabaseExecutionTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(PgJob.PostgreSql.PgDumpPath, "fixture");
        File.WriteAllText(PgJob.PostgreSql.PgRestorePath, "fixture");
    }

    private LocalBackupRunner Runner(IBackupExecutor executor) => new(executor,
        new BackupCompletion(new FileBackupStorage(), new BackupCatalog(Path.Combine(root, "catalog.json"))),
        new BackupRunHistory(Path.Combine(root, "history.json")), Path.Combine(root, "work"));

    [Fact]
    public async Task SqlCreatesCopyOnlyWithChecksumVerifiesThenDeliversAndPreservesServerOriginal()
    {
        var fixture = new SqlFixture();
        var result = await Runner(new SqlServerBackupExecutor(_ => fixture)).RunAsync(SqlJob with { ArchiveCopies = true });
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.Equal(2, fixture.Commands.Count);
        Assert.Contains("COPY_ONLY, CHECKSUM", fixture.Commands[0].Text);
        Assert.StartsWith("RESTORE VERIFYONLY", fixture.Commands[1].Text);
        Assert.Equal(SqlJob.Source, fixture.Commands[0].Values["@database"]);
        Assert.DoesNotContain(SqlJob.Source, fixture.Commands[0].Text);
        Assert.Equal(fixture.Commands[0].Values["@path"], fixture.Commands[1].Values["@path"]);
        Assert.True(File.Exists((string)fixture.Commands[0].Values["@path"]));
        using var zip = ZipFile.OpenRead(result.Backup.Location);
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal("SQL backup fixture", await reader.ReadToEndAsync());
        Assert.False(Directory.Exists(result.WorkDirectory));
    }

    [Fact]
    public async Task SqlFinalDirectoryCanBeItsNativeBackupDirectoryWithoutDuplicateCopy()
    {
        var job = SqlJob with { DestinationDirectory = SqlJob.SqlServer.AgentReadDirectory, KeepLast = 1 };
        var runner = Runner(new SqlServerBackupExecutor(_ => new SqlFixture()));
        var first = await runner.RunAsync(job);
        var second = await runner.RunAsync(job);
        Assert.Equal(BackupRunStatus.Succeeded, first.Status);
        Assert.Equal(BackupRunStatus.Succeeded, second.Status);
        Assert.False(File.Exists(first.Backup.Location));
        Assert.True(File.Exists(second.Backup.Location));
        Assert.Single(Directory.GetFiles(job.DestinationDirectory));
    }

    [Fact]
    public async Task FailedSqlVerificationDoesNotDeliverOrPrunePreviousSuccessfulCopy()
    {
        var previous = await Runner(new SqlServerBackupExecutor(_ => new SqlFixture())).RunAsync(SqlJob with { KeepLast = 1 });
        var bad = new SqlFixture { FailVerify = true };
        var failed = await Runner(new SqlServerBackupExecutor(_ => bad)).RunAsync(SqlJob with { KeepLast = 1 });
        Assert.Equal(BackupRunStatus.Failed, failed.Status);
        Assert.True(File.Exists(previous.Backup.Location));
        Assert.Single(Directory.GetFiles(SqlJob.DestinationDirectory));
        Assert.Empty(Directory.GetFiles(failed.WorkDirectory, "*.bak"));
        Assert.True(File.Exists(Path.Combine(failed.WorkDirectory, "sql-source.json")));
    }

    [Fact]
    public async Task SqlUnreadableMappedDirectoryDoesNotClaimSuccess()
    {
        var fixture = new SqlFixture();
        var job = SqlJob with { SqlServer = SqlJob.SqlServer with { AgentReadDirectory = Path.Combine(root, "wrong-map") } };
        var result = await Runner(new SqlServerBackupExecutor(_ => fixture)).RunAsync(job);
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Contains("агент не может прочитать", result.Error);
        Assert.False(Directory.Exists(job.DestinationDirectory));
    }

    [Fact]
    public async Task PostgreSqlRunsDumpAndCatalogCheckWithoutPasswordArgument()
    {
        var calls = new List<BackupProcess>();
        var process = new FixtureProcess(async (command, token) =>
        {
            calls.Add(command);
            if (command.Executable == PgJob.PostgreSql.PgDumpPath)
            {
                Assert.Contains("--no-password", command.Arguments);
                Assert.Contains("--dbname=database with spaces", command.Arguments);
                Assert.Contains("--username=backup-user", command.Arguments);
                Assert.Contains("--host=pg.example.com", command.Arguments);
                await File.WriteAllTextAsync(command.Arguments.Single(a => a.StartsWith("--file=")).Substring(7), "PG dump fixture", token);
            }
            else
            {
                Assert.Equal("--list", command.Arguments[0]);
                Assert.True(File.Exists(command.Arguments[1]));
            }
            return 0;
        });
        var result = await Runner(new DatabaseBackupExecutor(process)).RunAsync(PgJob);
        Assert.Equal(BackupRunStatus.Succeeded, result.Status);
        Assert.Equal(2, calls.Count);
        Assert.EndsWith(".dump", result.Backup.Location);
        Assert.Equal("PG dump fixture", await File.ReadAllTextAsync(result.Backup.Location));
    }

    [Theory]
    [InlineData(1, false, 0)]
    [InlineData(0, false, 0)]
    [InlineData(0, true, 1)]
    public async Task PostgreSqlFailureMissingDumpOrInvalidCatalogPreventsDelivery(int dumpCode, bool writeDump, int restoreCode)
    {
        var process = new FixtureProcess(async (command, token) =>
        {
            if (command.Executable == PgJob.PostgreSql.PgDumpPath)
            {
                if (writeDump) await File.WriteAllTextAsync(command.Arguments.Single(a => a.StartsWith("--file=")).Substring(7), "partial", token);
                return dumpCode;
            }
            return restoreCode;
        });
        var result = await Runner(new PostgreSqlBackupExecutor(process)).RunAsync(PgJob);
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.False(Directory.Exists(PgJob.DestinationDirectory));
    }

    [Fact]
    public async Task DatabaseSettingsSurviveReloadAndOldJobsRequireConfigurationOnlyWhenExecuting()
    {
        var store = new JobStore(Path.Combine(root, "jobs.json"));
        await store.SaveAsync([SqlJob, PgJob with { Id = Guid.NewGuid() }]);
        var jobs = await store.LoadAsync();
        Assert.Equal(SqlJob, jobs[0]);
        Assert.Equal(PgJob.PostgreSql, jobs[1].PostgreSql);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SqlServerBackupExecutor().CreateAsync(
            SqlJob with { SqlServer = null }, root, CancellationToken.None));
    }

    [Fact]
    public void PostgreSqlConnectionStringsAndInvalidSettingsAreRejected()
    {
        Assert.NotEmpty((PgJob with { Source = "host=pg password=secret" }).Validate());
        Assert.NotEmpty((PgJob with { PostgreSql = PgJob.PostgreSql with { Port = 0 } }).Validate());
        Assert.NotEmpty((SqlJob with { SqlServer = SqlJob.SqlServer with { ServerBackupDirectory = "relative" } }).Validate());
    }

    [Fact]
    public async Task SqlAndPostgreSqlAreAcceptedForScheduling()
    {
        var store = new JobStore(Path.Combine(root, "jobs.json"));
        await store.SaveAsync([SqlJob, PgJob with { Id = Guid.NewGuid() }]);
        Assert.Equal(2, (await new LocalBackupDispatcher(store, (_, _) => Task.CompletedTask).LoadScheduledJobsAsync()).Count);
    }

    private sealed class FixtureProcess(Func<BackupProcess, CancellationToken, Task<int>> run) : IBackupProcessRunner
    {
        public Task<int> RunAsync(BackupProcess command, CancellationToken token) => run(command, token);
    }
    private sealed record CapturedCommand(string Text, Dictionary<string, object> Values);
    private sealed class SqlFixture : DbConnection
    {
        public List<CapturedCommand> Commands { get; } = [];
        public bool FailVerify { get; init; }
        private ConnectionState state;
        public override string ConnectionString { get; set; } = "";
        public override string Database => "master";
        public override string DataSource => "fixture";
        public override string ServerVersion => "fixture";
        public override ConnectionState State => state;
        public override void Open() => state = ConnectionState.Open;
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FixtureCommand(this);
    }
    private sealed class FixtureCommand(SqlFixture owner) : DbCommand
    {
        private readonly SqlCommand parameters = new();
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection DbConnection { get; set; } = owner;
        protected override DbTransaction DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        public override int ExecuteNonQuery()
        {
            var values = parameters.Parameters.Cast<SqlParameter>().ToDictionary(p => p.ParameterName, p => p.Value);
            owner.Commands.Add(new CapturedCommand(CommandText, values));
            if (CommandText.StartsWith("BACKUP"))
            {
                var path = (string)values["@path"];
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "SQL backup fixture");
            }
            else if (owner.FailVerify) throw new IOException("SQL verification failed");
            return -1;
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override void Cancel() { }
        public override void Prepare() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) parameters.Dispose(); base.Dispose(disposing); }
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
