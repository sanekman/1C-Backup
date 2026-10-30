using System.Text;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class OneCDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-discovery-" + Guid.NewGuid());
    public OneCDiscoveryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void LauncherParserKeepsFilePathAndVersionWithoutImportingCredentialsOrServerBases()
    {
        var bases = OneCUserBases.Parse("""
            [Бухгалтерия]
            Connect=File="D:\Базы;архив\Бухгалтерия";Usr="secret-user";Pwd="secret-password";
            Version=8.3.25.1540
            [Управление]
            Connect=File="\\server\Базы\Управление";
            Version=
            [Серверная]
            Connect=Srvr="server";Ref="database";
            Version=8.3
            [Повреждённая]
            Connect=File="unclosed
            """);
        Assert.Equal(2, bases.Count);
        Assert.Equal(@"D:\Базы;архив\Бухгалтерия", bases[0].Directory);
        Assert.Equal("8.3.25.1540", bases[0].PlatformVersion);
        Assert.Equal(@"\\server\Базы\Управление", bases[1].Directory);
        Assert.Empty(bases[1].PlatformVersion);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(bases));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("cp1251")]
    public void ReadsUserAndCommonListsInLauncherEncodingAndKeepsUserVersionOnDuplicates(string encoding)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codec = encoding switch { "utf16" => Encoding.Unicode, "cp1251" => Encoding.GetEncoding(1251), _ => new UTF8Encoding(true) };
        var user = Path.Combine(root, "user"); Directory.CreateDirectory(user);
        var common = Path.Combine(root, "общие.v8i");
        File.WriteAllText(Path.Combine(user, "ibases.v8i"), "[Бухгалтерия]\nConnect=File=\"D:\\База\";\nVersion=8.3.25.1540\n", codec);
        File.WriteAllText(common, "[Дубликат]\nConnect=File=\"D:\\База\";\nVersion=8.3\n[Вторая]\nConnect=File=\"D:\\Вторая\";\n[Серверная]\nConnect=Srvr=\"server\";Ref=\"database\";\n", codec);
        File.WriteAllText(Path.Combine(user, "1CEStart.cfg"), "CommonInfoBases=\"" + common + "\"\n", codec);
        var list = OneCUserBases.Read(user);
        Assert.Equal(2, list.Bases.Count);
        Assert.Equal("8.3.25.1540", list.Bases.Single(b => b.Name == "Бухгалтерия").PlatformVersion);
        Assert.Equal(1, list.ServerBases);
        Assert.Equal(0, list.UnreadableLists);
    }

    private OneCInstalledPlatform Install(string version, string folder = "platforms")
    {
        var executable = Path.Combine(root, folder, version, "bin", "1cv8.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!); File.WriteAllText(executable, "fixture");
        return new(Version.Parse(version), executable);
    }

    [Theory]
    [InlineData("", "8.5.1.100")]
    [InlineData("8.5", "8.5.1.100")]
    [InlineData("8.3", "8.3.25.1540")]
    [InlineData("8.3.*", "8.3.25.1540")]
    [InlineData("8.3.9", "8.3.9.200")]
    [InlineData("8.3.9.100", "8.3.9.100")]
    public void ChoosesNumericallyNewestMatchingVersionAndUsesNewestWhenPropertyIsEmpty(string requested, string expected)
    {
        var installed = new[] { Install("8.3.9.100"), Install("8.3.25.1540"), Install("8.4.1.100"), Install("8.5.1.100"), Install("8.3.9.200") };
        var settings = new OneCSettings { AutoSelectPlatform = true, PlatformVersion = requested };
        Assert.Equal(installed.Single(p => p.Version.ToString() == expected).ExecutablePath, OneCPlatformLocator.Resolve(settings, installed));
    }

    [Fact]
    public void MissingRequestedVersionDoesNotFallBackAndLatestIsResolvedAgainAfterInstallingNewVersion()
    {
        var installed = new List<OneCInstalledPlatform> { Install("8.3.25.1540") };
        var settings = new OneCSettings { AutoSelectPlatform = true };
        Assert.Equal(installed[0].ExecutablePath, OneCPlatformLocator.Resolve(settings, installed));
        Assert.Throws<FileNotFoundException>(() => OneCPlatformLocator.Resolve(settings with { PlatformVersion = "8.3.24.1000" }, installed));
        installed.Add(Install("8.3.26.1000"));
        Assert.Equal(installed[1].ExecutablePath, OneCPlatformLocator.Resolve(settings, installed));
        Assert.Equal(2, OneCPlatformLocator.Discover([Path.Combine(root, "platforms")]).Count);
        File.Delete(installed[1].ExecutablePath);
        Assert.Equal(installed[0].ExecutablePath, OneCPlatformLocator.Resolve(settings, installed));
    }

    [Theory]
    [InlineData("../../8.3")]
    [InlineData("8.3 beta")]
    [InlineData("8")]
    [InlineData("8.3.-1")]
    public void RejectsMalformedVersionWithoutTreatingItAsLatest(string version) =>
        Assert.NotEmpty(new OneCSettings { AutoSelectPlatform = true, PlatformVersion = version }.Validate());

    [Fact]
    public async Task StoredAutoSelectionRunsResolvedExecutableWhileLegacyExplicitPathIsPreserved()
    {
        var first = Install("8.3.25.1540"); var newer = Install("8.3.26.1000");
        var source = Path.Combine(root, "base"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "1Cv8.1CD"), "fixture");
        var job = new BackupJob { Name = "База", Source = source, DestinationDirectory = Path.Combine(root, "copies"),
            OneC = new() { AutoSelectPlatform = true, PlatformVersion = "" } };
        var store = new JobStore(Path.Combine(root, "jobs.json")); await store.SaveAsync([job]);
        var saved = Assert.Single(await store.LoadAsync()); Assert.True(saved.OneC!.AutoSelectPlatform);
        var process = new Process();
        var executor = new OneCBackupExecutor(process, platformResolver: settings => OneCPlatformLocator.Resolve(settings, [first, newer]));
        await executor.CreateAsync(saved, Path.Combine(root, "work"), CancellationToken.None);
        Assert.Equal(newer.ExecutablePath, process.Executable);
        await executor.CreateAsync(saved with { OneC = new() { ExecutablePath = first.ExecutablePath } }, Path.Combine(root, "manual"), CancellationToken.None);
        Assert.Equal(first.ExecutablePath, process.Executable);
    }
    private sealed class Process : IBackupProcessRunner
    {
        public string? Executable;
        public async Task<int> RunAsync(BackupProcess command, CancellationToken token)
        { Executable = command.Executable; await File.WriteAllTextAsync(command.Arguments[4], "dump", token); return 0; }
    }
    public void Dispose() => Directory.Delete(root, true);
}
