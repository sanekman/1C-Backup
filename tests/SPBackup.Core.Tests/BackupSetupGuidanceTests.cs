using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupSetupGuidanceTests
{
    [Theory]
    [InlineData(DatabaseKind.SqlServer, @"C:\backup\base.bak")]
    [InlineData(DatabaseKind.PostgreSql, "postgresql://user@db/accounting")]
    [InlineData(DatabaseKind.SqlServer, "Server=DB;Database=Accounting;")]
    [InlineData(DatabaseKind.PostgreSql, "/var/backups/db.dump")]
    public void DatabaseFieldExplainsCommonPastedAddressAndPathMistakes(DatabaseKind kind, string source)
        => Assert.NotNull(BackupSetupGuidance.DatabaseNameError(kind, source));
    [Theory]
    [InlineData("Accounting")]
    [InlineData("Бухгалтерия 2026")]
    [InlineData("db.with.dots")]
    public void DatabaseNamesAreNotRestrictedToAsciiIdentifiers(string source)
        => Assert.Null(BackupSetupGuidance.DatabaseNameError(DatabaseKind.SqlServer, source));
    [Theory]
    [InlineData(DatabaseKind.SqlServer, "https://db.example.ru")]
    [InlineData(DatabaseKind.PostgreSql, "db.example.ru:5432")]
    public void ServerAddressMistakesExplainSeparateFields(DatabaseKind kind, string server)
        => Assert.NotNull(BackupSetupGuidance.DatabaseServerError(kind, server));
    [Theory]
    [InlineData("::1")]
    [InlineData("[2001:db8::1]")]
    [InlineData("db.example.ru")]
    public void ValidPostgreSqlHostsIncludingIpv6RemainSupported(string host)
        => Assert.Null(BackupSetupGuidance.DatabaseServerError(DatabaseKind.PostgreSql, host));

    [Fact]
    public void EachSupportedSourceHasAProcedureWithServiceAndRestoreBoundaries()
    {
        foreach (var kind in Enum.GetValues<DatabaseKind>())
        {
            Assert.NotEmpty(BackupSetupGuidance.SourceExample(kind));
            var steps = BackupSetupGuidance.FirstRun(kind);
            Assert.Contains("5.", steps); Assert.Contains("Проводника", steps); Assert.Contains("восстанов", steps);
            Assert.Contains(kind is DatabaseKind.SqlServer or DatabaseKind.PostgreSql ? "тестовую базу" : kind == DatabaseKind.OneCFile ? "Рабочую базу" : "пустую папку", steps);
        }
    }
}
