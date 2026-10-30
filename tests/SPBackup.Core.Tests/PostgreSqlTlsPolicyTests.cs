using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class PostgreSqlTlsPolicyTests
{
    [Fact]
    public void VerifyFullOverridesInheritedModeAndAddressWithoutChangingParentEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "trusted-ca.pem");
        var previous = Environment.GetEnvironmentVariable("PGSSLMODE");
        var settings = PostgreSqlTlsPolicy.VerifyFull(root);
        Assert.Equal("verify-full", settings["PGSSLMODE"]);
        Assert.Null(settings["PGHOSTADDR"]);
        Assert.Equal(root, settings["PGSSLROOTCERT"]);
        Assert.Equal(previous, Environment.GetEnvironmentVariable("PGSSLMODE"));
    }

    [Fact]
    public void DefaultTrustRemovesInheritedRootOverride()
    {
        var settings = PostgreSqlTlsPolicy.VerifyFull(null);
        Assert.Null(settings["PGSSLROOTCERT"]);
        Assert.Equal("verify-full", settings["PGSSLMODE"]);
    }

    [Fact]
    public async Task ChildProcessReceivesProtectedSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "trusted-ca.pem");
        var command = new BackupProcess(OperatingSystem.IsWindows() ? "python" : "python3",
            ["-c", "import os,sys; sys.exit(0 if os.environ.get('PGSSLMODE') == 'verify-full' and os.environ.get('PGGSSENCMODE') == 'disable' and 'PGHOSTADDR' not in os.environ and os.environ.get('PGSSLROOTCERT') == sys.argv[1] else 1)", root],
            Path.GetTempPath()) { Environment = PostgreSqlTlsPolicy.VerifyFull(root) };
        Assert.Equal(0, await new BackupProcessRunner().RunAsync(command, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.pem")]
    [InlineData("invalid\0certificate")]
    public void InvalidCertificatePathIsRejected(string path) =>
        Assert.Throws<ArgumentException>(() => PostgreSqlTlsPolicy.VerifyFull(path));
}
