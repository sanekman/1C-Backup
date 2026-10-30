using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class PostgreSqlToolDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-pg-tools-" + Guid.NewGuid());
    private string Installation(string version, bool restore = true, bool psql = true)
    {
        var path = Path.Combine(root, version); var bin = Path.Combine(path, "bin"); Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "pg_dump.exe"), "not executable; discovery must not run it");
        if (restore) File.WriteAllText(Path.Combine(bin, "pg_restore.exe"), "fixture");
        if (psql) File.WriteAllText(Path.Combine(bin, "psql.exe"), "fixture");
        return path;
    }

    [Fact]
    public void CompletePairsAreSortedByNumericVersionAndDeduplicated()
    {
        var ten = Installation("10"); var seventeen = Installation("17"); var nine = Installation("9.6");
        var found = PostgreSqlToolDiscovery.Find([ten, seventeen, nine, Path.Combine(seventeen, "bin"), seventeen]);
        Assert.Equal(new[] { "17", "10", "9.6" }, found.Select(item => item.Version));
        foreach (var item in found)
        {
            Assert.Equal(Path.GetDirectoryName(item.PgDumpPath), Path.GetDirectoryName(item.PgRestorePath));
            Assert.True(item.HasPsql);
        }
    }

    [Fact]
    public void IncompleteInstallationsDoNotMixProgramsFromDifferentVersions()
    {
        var incomplete = Installation("18", restore: false); var complete = Installation("17", psql: false);
        var found = Assert.Single(PostgreSqlToolDiscovery.Find([incomplete, complete, "relative", Path.Combine(root, "missing")]));
        Assert.Equal("17", found.Version); Assert.False(found.HasPsql);
    }

    [Fact]
    public void UnknownFolderNamesRemainAvailableWithoutExecutingThem()
    {
        var custom = Installation("Postgres Tools");
        var found = Assert.Single(PostgreSqlToolDiscovery.Find([custom]));
        Assert.Contains("Postgres Tools", found.Label);
        Assert.Equal("not executable; discovery must not run it", File.ReadAllText(found.PgDumpPath));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
