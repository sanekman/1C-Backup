using System.Net;
using System.Text;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class OneCCredentialTests : IDisposable
{
    private const string Password="private пароль 🔐 /WA+ with\\backslash";
    private readonly string root=Path.Combine(Path.GetTempPath(),"sp-onec-login-"+Guid.NewGuid().ToString("N"));
    private BackupJob Job => new() { Name="protected DT",Source=Path.Combine(root,"source"),DestinationDirectory=Path.Combine(root,"copies"),
        OneCMethod=OneCBackupMethod.PlatformDump,OneC=new() { ExecutablePath=Path.Combine(root,"1cv8.exe"),Username="ib-user",CredentialKey="owned-key" } };
    public OneCCredentialTests()
    { Directory.CreateDirectory(Job.Source); File.WriteAllText(Path.Combine(Job.Source,"1Cv8.1CD"),"untouched source"); File.WriteAllText(Job.OneC!.ExecutablePath,"controller fixture only"); }
    private sealed class Credentials(string username="ib-user") : IOneCCredentialStore
    { public Task<NetworkCredential> GetAsync(string key,CancellationToken token=default) { token.ThrowIfCancellationRequested(); return Task.FromResult(new NetworkCredential(username,Password)); } }
    private sealed class Process(Func<BackupProcess,CancellationToken,Task<int>> action) : IBackupProcessRunner
    { public Task<int> RunAsync(BackupProcess command,CancellationToken token)=>action(command,token); }

    [Theory]
    [InlineData("success")]
    [InlineData("exit-error")]
    [InlineData("start-error")]
    [InlineData("cancel")]
    public async Task PasswordStaysOutOfArgumentsWorkAndJobsAndParameterFileIsRemoved(string outcome)
    {
        var work=Path.Combine(root,"work");string? parameters=null;
        using var stop=new CancellationTokenSource();
        var process=new Process(async (command,token)=>
        {
            Assert.Equal("/@",command.Arguments[0]); Assert.Equal(2,command.Arguments.Count);
            parameters=command.Arguments[1];Assert.False(parameters.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(Password,JsonSerializer.Serialize(command));
            var bytes=await File.ReadAllBytesAsync(parameters,token);
            Assert.Equal(new byte[] { 0xEF,0xBB,0xBF },bytes[..3]);
            var text=new UTF8Encoding(false,true).GetString(bytes,3,bytes.Length-3);
            Assert.StartsWith("DESIGNER /F ",text);Assert.Contains(" /WA- ",text);Assert.Contains("/N \"ib-user\"",text);Assert.Contains("/P \""+Password+"\"",text);
            Assert.Empty(Directory.GetFiles(work));
            if (!OperatingSystem.IsWindows())
            { Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(parameters)); Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute,File.GetUnixFileMode(Path.GetDirectoryName(parameters)!)); }
            if(outcome=="start-error")throw new IOException("owned fixture start failure");
            if(outcome=="cancel") { stop.Cancel();token.ThrowIfCancellationRequested(); }
            if(outcome=="exit-error")return 1;
            var target=text.Split('"').Single(value=>value.EndsWith(".dt",StringComparison.Ordinal));
            await File.WriteAllTextAsync(target,"DT controller fixture",token);return 0;
        });
        var executor=new OneCBackupExecutor(process,credentials:new Credentials());
        var task=executor.CreateAsync(Job,work,stop.Token);
        if(outcome=="success")Assert.True(File.Exists(await task));
        else if(outcome=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
        else await Assert.ThrowsAnyAsync<IOException>(()=>task);
        Assert.NotNull(parameters);Assert.False(File.Exists(parameters));Assert.False(Directory.Exists(Path.GetDirectoryName(parameters)));
        Assert.DoesNotContain(Password,JsonSerializer.Serialize(Job));
        Assert.Equal("untouched source",await File.ReadAllTextAsync(Path.Combine(Job.Source,"1Cv8.1CD")));
    }

    [Fact]
    public async Task WrongCredentialBindingIsRejectedBeforeStartingPlatform()
    {
        var process=new Process((_,_)=>throw new InvalidOperationException("must not launch"));
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>new OneCBackupExecutor(process,credentials:new Credentials("other-user"))
            .CreateAsync(Job,Path.Combine(root,"work"),default));
        Assert.DoesNotContain(Password,error.Message);
        Assert.NotEmpty((Job with { OneC=Job.OneC! with { CredentialKey=null } }).Validate());
    }

    [Fact]
    public async Task SavedInfobaseJobIsIsolatedFromTheLegacyJobFile()
    {
        var file=Path.Combine(root,"jobs.json");var store=new JobStore(file);
        await store.SaveAsync([Job]);
        Assert.Empty(JsonSerializer.Deserialize<JsonElement[]>(await File.ReadAllTextAsync(file))!);
        var loaded=Assert.Single(await store.LoadAsync()); Assert.Equal("owned-key",loaded.OneC!.CredentialKey);
        Assert.DoesNotContain(Password,await File.ReadAllTextAsync(file+".protected.json"));
        // A legacy editor cannot erase a protected DT job by rewriting its own ordinary list.
        await File.WriteAllTextAsync(file,"[]"); Assert.Equal(loaded.Id,Assert.Single(await store.LoadAsync()).Id);
    }

    [Fact]
    public async Task SourceProbeChecksSecretAvailabilityWithoutLaunchingOrClaimingLogin()
    {
        var process=new Process((_,_)=>throw new InvalidOperationException("source probe must not launch"));
        var checker=new SourceReadinessChecker(process,Path.Combine(root,"probe"),oneCCredentials:new Credentials());
        var result=await checker.CheckAsync(Job); Assert.True(result.Succeeded);
        Assert.Contains("Вход в 1С и право выгрузки проверяются",result.Details);Assert.DoesNotContain(Password,result.Details);
        var wrong=await new SourceReadinessChecker(process,Path.Combine(root,"probe"),oneCCredentials:new Credentials("other" )).CheckAsync(Job);
        Assert.False(wrong.Succeeded);
    }
    [Theory]
    [InlineData(65001,1)]
    [InlineData(1251,1)]
    [InlineData(1200,1)]
    [InlineData(65001,0)]
    public async Task AuthenticationFailureHasSafeGuidanceWithoutRawPlatformText(int codepage,int exitCode)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var process=new Process(async (command,token)=>
        {
            Assert.Contains("/WA-",command.Arguments);
            var log=command.Arguments[6];
            await File.WriteAllBytesAsync(log,Encoding.GetEncoding(codepage).GetBytes("Пользователь ИБ не идентифицирован\nprivate-business-text "+Password),token);
            return exitCode;
        });
        var job=Job with { OneC=Job.OneC! with { Username=null,CredentialKey=null } };
        var failure=await Assert.ThrowsAnyAsync<Exception>(()=>new OneCBackupExecutor(process).CreateAsync(job,Path.Combine(root,"work"),default));
        Assert.Contains("Задайте пользователя и пароль",failure.Message);
        Assert.DoesNotContain("private-business-text",failure.Message);Assert.DoesNotContain(Password,failure.Message);
    }
    public void Dispose() { if(Directory.Exists(root))Directory.Delete(root,true); }
}
