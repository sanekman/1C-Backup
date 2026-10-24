using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class SqlServerErrorHintsTests
{
    private const string PrivateText = "private-server private-user password=fixture-secret";
    [Theory]
    [InlineData(18456, "отклонил вход")]
    [InlineData(18487, "истёк")]
    [InlineData(18488, "сменить")]
    [InlineData(4060, "открыть базу")]
    [InlineData(229, "Недостаточно прав")]
    [InlineData(-2, "отведённое время")]
    [InlineData(53, "связаться")]
    [InlineData(10061, "связаться")]
    [InlineData(10054, "прервано")]
    public void KnownSqlCodesExplainActionWithoutRepeatingDriverText(int code, string expected)
    {
        var error = SqlError(code);
        var hint = SqlServerErrorHints.Describe(error)!;
        Assert.Contains(expected, hint); Assert.Contains(code.ToString(), hint);
        Assert.DoesNotContain(PrivateText, hint); Assert.DoesNotContain("fixture-secret", hint);
        Assert.Equal(code, DiagnosticException.Capture(error).NativeCode);
    }

    [Theory]
    [InlineData(unchecked((int)0x800B0109), "недоверенным")]
    [InlineData(unchecked((int)0x800B010A), "цепочку")]
    [InlineData(unchecked((int)0x800B010C), "отозван")]
    [InlineData(unchecked((int)0x80090325), "доверие")]
    [InlineData(unchecked((int)0x800B010F), "не совпадает")]
    [InlineData(unchecked((int)0x800B0101), "срок")]
    [InlineData(unchecked((int)0x80090328), "срок")]
    public void NativeCertificateCodesSurviveWrappingAndDoNotSuggestDisablingTls(int code, string expected)
    {
        var error = new IOException(PrivateText, new Win32Exception(code, PrivateText));
        var hint = SqlServerErrorHints.Describe(error)!;
        Assert.Contains(expected, hint); Assert.Contains(unchecked((uint)code).ToString("X8"), hint);
        Assert.DoesNotContain(PrivateText, hint); Assert.DoesNotContain("TrustServerCertificate=true", hint);
    }

    [Fact]
    public void WrongPrincipalCanBeCertificateOrWindowsAuthenticationAndDoesNotGuessWhich()
    {
        var hint = SqlServerErrorHints.Describe(new Win32Exception(unchecked((int)0x80090322), PrivateText))!;
        Assert.Contains("аутентификации", hint); Assert.Contains("Код Windows", hint);
        Assert.DoesNotContain("Код сертификата", hint); Assert.DoesNotContain(PrivateText, hint);
    }

    [Fact]
    public void SpecificCertificateEvidenceWinsOverGenericNetworkError()
    {
        var hint = SqlServerErrorHints.Describe(new AggregateException(SqlError(53), new Win32Exception(unchecked((int)0x800B010F), PrivateText)))!;
        Assert.Contains("не совпадает", hint); Assert.DoesNotContain("связаться", hint);
    }

    [Fact]
    public void UnknownErrorsAndLocalizedMessagesAreNotGuessedFromText()
    {
        Assert.Null(SqlServerErrorHints.Describe(SqlError(3013)));
        Assert.Null(SqlServerErrorHints.Describe(new IOException("Login failed: SSL certificate password authentication failed " + PrivateText)));
        Assert.Contains("TLS", SqlServerErrorHints.Describe(new AuthenticationException(PrivateText))!);
        Assert.Null(SqlServerErrorHints.Describe(new OperationCanceledException(PrivateText, SqlError(18456))));
    }

    [Fact]
    public async Task SourceCheckAndSqlBackupUseSameSafeExplanationAndPreserveOriginalDiagnosticCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-sql-hints-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var job = new BackupJob { Name = "SQL hint fixture", Kind = DatabaseKind.SqlServer, Source = "fixture",
                DestinationDirectory = Path.Combine(root, "copies"), SqlServer = new() { Server = "sql.example",
                    ServerBackupDirectory = root, AgentReadDirectory = root } };
            var error = SqlError(18456);
            var checker = new SourceReadinessChecker(new NoProcess(), root, _ => new FailingConnection(error));
            var report = await checker.CheckAsync(job);
            Assert.False(report.Succeeded); Assert.False(report.Cancelled);
            Assert.Equal(SqlServerErrorHints.Describe(error), report.Details);
            Assert.Empty(Directory.GetFileSystemEntries(root));
            var failure = await Assert.ThrowsAsync<IOException>(() => new SqlServerBackupExecutor(_ => new FailingConnection(error)).CreateAsync(job, root, default));
            Assert.Equal(report.Details, failure.Message); Assert.Same(error, failure.InnerException);
            Assert.Equal(18456, Assert.Single(DiagnosticException.Capture(failure).Inner).NativeCode);
            Assert.Equal("sql-source.json", Path.GetFileName(Assert.Single(Directory.GetFiles(root))));
            Assert.False(Directory.Exists(job.DestinationDirectory));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HistoryAndAutomaticPollingUseSameExplanationWithoutDiscardingPreviousHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-monitor-hints-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var error = SqlError(229); var source = new SqlServerBackupHistorySource(_ => new FailingConnection(error));
            var monitor = new ExternalBackupMonitor { Name = "SQL", SqlServer = "sql.example" };
            var failure = await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(monitor));
            Assert.Equal(SqlServerErrorHints.Describe(error), failure.Message); Assert.Same(error, failure.InnerException);
            var store = new ExternalBackupMonitorStore(Path.Combine(root, "monitors.json")); await store.SaveAsync([monitor]);
            var outbox = new ExternalBackupOutbox(Path.Combine(root, "history.json")); var settings = new AgentSettings();
            var prior = new ExternalBackupObservation("existing", "fixture", "database", "D", new DateTime(2026, 1, 1),
                new DateTime(2026, 1, 1, 1, 0, 0), "UTC", ExternalBackupOutcome.Completed, 10, [], "SqlServerMsdbHistory");
            await outbox.ObserveAsync(settings, monitor, [prior]);
            var before = await File.ReadAllBytesAsync(Path.Combine(root, "history.json"));
            var status = new ExternalBackupPollingStatusStore(root);
            var poller = new ExternalBackupPoller(store, outbox, settings, status, _ => source);
            var result = Assert.Single(await poller.PollAsync(DateTimeOffset.UtcNow));
            Assert.Equal(failure.Message, result.Error); Assert.Equal(0, result.Added);
            Assert.Equal(prior.SourceKey, Assert.Single(await outbox.ReadAsync()).Observation.SourceKey);
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, "history.json")));
            Assert.Equal(result.Error, Assert.Single(await status.ReadAsync()).Error);
            Assert.DoesNotContain(PrivateText, await File.ReadAllTextAsync(Path.Combine(root, "external-poll-status.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationIsStillCancellationAndUnknownBackupErrorRetainsItsDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-sql-cancel-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var job = new BackupJob { Name = "SQL fixture", Kind = DatabaseKind.SqlServer, Source = "fixture",
                DestinationDirectory = Path.Combine(root, "copies"), SqlServer = new() { Server = "sql.example",
                    ServerBackupDirectory = root, AgentReadDirectory = root } };
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var checker = new SourceReadinessChecker(new NoProcess(), root, _ => new FailingConnection(new OperationCanceledException(cancelled.Token)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(job, cancelled.Token));
            Assert.Empty(Directory.GetFileSystemEntries(root));
            var error = SqlError(3013);
            var failure = await Assert.ThrowsAsync<SqlException>(() => new SqlServerBackupExecutor(_ => new FailingConnection(error)).CreateAsync(job, root, default));
            Assert.Same(error, failure);
        }
        finally { Directory.Delete(root, true); }
    }

    // SqlClient does not expose constructors for server errors. Build a provider exception with its pinned assembly.
    private static SqlException SqlError(int code)
    {
        var constructor = typeof(SqlError).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderBy(ctor => ctor.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select((p, index) => index == 0 ? (object)code : p.ParameterType == typeof(string)
            ? PrivateText : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var error = constructor.Invoke(arguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(errors, [error]);
        return (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "CreateException" && m.GetParameters().Length == 2).Invoke(null, [errors, "fixture-version"])!;
    }
    private sealed class NoProcess : IBackupProcessRunner
    {
        public Task<int> RunAsync(BackupProcess command, CancellationToken token) => throw new InvalidOperationException("No process expected");
    }
    private sealed class FailingConnection(Exception error) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "master";
        public override string DataSource => "fixture";
        public override string ServerVersion => "fixture";
        public override ConnectionState State => ConnectionState.Closed;
        public override void Open() => throw error;
        public override Task OpenAsync(CancellationToken token) => Task.FromException(error);
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
