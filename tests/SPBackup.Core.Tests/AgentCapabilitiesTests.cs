using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class AgentCapabilitiesTests
{
    [Fact]
    public void CurrentLibraryReportsParsingWithoutClaimingManagedExecution()
    {
        var claims = AgentCapabilities.Parse(AgentCapabilities.HeaderValue);
        Assert.Contains(AgentCapabilities.StrictConfigurationV1, claims);
        Assert.Contains(AgentCapabilities.PostgreSqlTlsV1, claims);
        Assert.DoesNotContain(AgentCapabilities.ManagedExecutionV1, claims);
        Assert.Empty(AgentCapabilities.Parse(null));
        Assert.Equal(new[] { "configuration-v1", "future-feature-v2" }, AgentCapabilities.Parse("future-feature-v2, configuration-v1, configuration-v1"));
    }

    [Theory]
    [InlineData("bad token")]
    [InlineData("UPPERCASE")]
    [InlineData("configuration-v1,,managed-execution-v1")]
    [InlineData("-invalid")]
    [InlineData("token:secret")]
    [InlineData("cap\r\nvalue")]
    public void MalformedClaimsAreRejected(string value) => Assert.Throws<ArgumentException>(() => AgentCapabilities.Parse(value));

    [Fact]
    public void ClaimAndHeaderLimitsAreBounded()
    {
        Assert.Throws<ArgumentException>(() => AgentCapabilities.Parse(new string('a', 2049)));
        Assert.Throws<ArgumentException>(() => AgentCapabilities.Parse(new string('a', 65)));
        Assert.Throws<ArgumentException>(() => AgentCapabilities.Parse(string.Join(",", Enumerable.Range(0, 33).Select(i => "cap-" + i))));
    }
}
