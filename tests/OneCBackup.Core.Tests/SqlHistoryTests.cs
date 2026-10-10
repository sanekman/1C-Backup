#nullable disable
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class SqlHistoryTests
{
    [Fact]
    public async Task GroupsStripedBackupDevicesWithoutInventingFileVerification()
    {
        var table = Table();
        var id = Guid.NewGuid();
        table.Rows.Add("SERVER\\SQL", id, "database", "D", new DateTime(2026, 10, 10, 23, 0, 0),
            new DateTime(2026, 10, 10, 23, 5, 0), 500m, "D:\\backup-part1.bak");
        table.Rows.Add("SERVER\\SQL", id, "database", "D", new DateTime(2026, 10, 10, 23, 0, 0),
            new DateTime(2026, 10, 10, 23, 5, 0), 500m, "D:\\backup-part2.bak");
        var connection = new FixtureConnection(table);
        var source = new SqlServerBackupHistorySource(_ => connection);
        var row = Assert.Single(await source.ReadAsync(new ExternalBackupMonitor
        { Name = "SQL", SqlServer = "SERVER\\SQL", TimeZoneId = "Asia/Vladivostok", LookbackDays = 7 }));
        Assert.Equal(2, row.Locations.Count);
        Assert.Equal(500m, row.SizeBytes);
        Assert.Equal(DateTimeKind.Unspecified, row.FinishedAt.Kind);
        Assert.Equal("Asia/Vladivostok", row.TimeZoneId);
        Assert.Equal("SqlServerMsdbHistory", row.Evidence);
        Assert.Equal(ExternalBackupOutcome.Completed, row.Outcome);
        Assert.Contains(id.ToString("D"), row.SourceKey);
    }

    [Fact]
    public async Task DatabaseFilterIsParameterAndReadUsesNoBackupOrDeleteCommands()
    {
        var connection = new FixtureConnection(Table());
        var database = "db'; DROP DATABASE other; --";
        Assert.Empty(await new SqlServerBackupHistorySource(_ => connection).ReadAsync(new ExternalBackupMonitor
        { Name = "SQL", SqlServer = "SERVER", DatabaseName = database }));
        Assert.DoesNotContain(database, connection.Sql);
        Assert.Equal(database, connection.Values["@database"]);
        Assert.Equal(30, connection.Values["@days"]);
        Assert.StartsWith("SELECT", connection.Sql);
        Assert.Contains("backup_finish_date IS NOT NULL", connection.Sql);
    }

    private static DataTable Table()
    {
        var table = new DataTable();
        foreach (var (name, type) in new[] { ("instance", typeof(string)), ("id", typeof(Guid)),
            ("database", typeof(string)), ("type", typeof(string)), ("start", typeof(DateTime)),
            ("finish", typeof(DateTime)), ("size", typeof(decimal)), ("device", typeof(string)) })
            table.Columns.Add(name, type);
        return table;
    }

    private sealed class FixtureConnection(DataTable table) : DbConnection
    {
        private ConnectionState state;
        public string Sql { get; set; }
        public Dictionary<string, object> Values { get; } = [];
        public override string ConnectionString { get; set; } = "";
        public override string Database => "msdb";
        public override string DataSource => "fixture";
        public override string ServerVersion => "fixture";
        public override ConnectionState State => state;
        public override void Open() => state = ConnectionState.Open;
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FixtureCommand(this, table);
    }

    private sealed class FixtureCommand(FixtureConnection owner, DataTable table) : DbCommand
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
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            owner.Sql = CommandText;
            foreach (SqlParameter parameter in parameters.Parameters) owner.Values[parameter.ParameterName] = parameter.Value;
            return table.CreateDataReader();
        }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object ExecuteScalar() => throw new NotSupportedException();
        public override void Cancel() { }
        public override void Prepare() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) parameters.Dispose(); base.Dispose(disposing); }
    }
}
