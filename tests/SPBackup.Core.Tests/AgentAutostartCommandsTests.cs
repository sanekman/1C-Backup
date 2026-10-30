using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class AgentAutostartCommandsTests
{
    [Theory]
    [InlineData("SPBackup.Desktop.exe", "--tray --start-agent")]
    [InlineData("OneCBackup.Desktop.exe", "--tray --start-agent")]
    [InlineData("SPBackup.Agent.exe", "--run --quiet")]
    [InlineData("OneCBackup.Agent.exe", "--run --quiet")]
    public void RecognizesOnlyKnownCommandsFromThisInstallation(string executable, string arguments)
    {
        var root = Path.GetFullPath("installed-agent"); var commands = new AgentAutostartCommands(root);
        Assert.True(commands.Owns($"\"{Path.Combine(root, executable)}\" {arguments}"));
        Assert.False(commands.Owns($"\"{Path.Combine(root + "-portable", executable)}\" {arguments}"));
        Assert.True(commands.Owns(commands.Current.ToUpperInvariant()));
    }

    [Fact]
    public void AnotherPortableInstallationOrCustomCommandMustNotBeOverwritten()
    {
        var commands = new AgentAutostartCommands(Path.GetFullPath("installed-agent"));
        Assert.False(commands.CanEnable(new AgentAutostartCommands(Path.GetFullPath("another-copy")).Current));
        Assert.False(commands.Owns(commands.Current + " --data-dir other-profile"));
        Assert.False(commands.CanEnable(""));
        Assert.True(commands.CanEnable(null)); Assert.True(commands.CanEnable(commands.Current));
    }
}
