using System.Net;
using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class ConfigurationReceiptTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sp-receipts-" + Guid.NewGuid());
    private string CachePath => Path.Combine(root, "server-jobs.json");
    private AgentConfigurationCache Cache => new(CachePath);
    private readonly AgentSettings settings = new() { Mode = AgentMode.Managed, ServerAddress = "https://backup.example.test/", CredentialKey = "test" };
    private ServerConfiguration Document(long revision) => new(settings.AgentId, revision,
        [new BackupJob { Kind = DatabaseKind.FilesAndFolders, Name = "Documents", Source = Path.Combine(root,"input"), DestinationDirectory = Path.Combine(root,"copies") }]);
    private AgentConfiguration Client(Api api) => new(new JobStore(Path.Combine(root, "jobs.json")), Cache, api);
    private sealed class Api : IAgentApi, IAgentConfigurationReceiptApi
    {
        public AgentConfigurationOffer Offer = null!;
        public Action<long?>? OnGet;
        public Exception? GetError, SendError;
        public Func<CancellationToken, Task>? SendCallback;
        public List<ConfigurationReceipt> Sent = [];
        public List<long?> Revisions = [];
        public Task<ServerConfiguration?> GetConfigurationAsync(AgentSettings s, long? r, CancellationToken token) => throw new InvalidOperationException("Use offer protocol");
        public Task SendHeartbeatAsync(AgentSettings s, AgentHeartbeat h, CancellationToken token) => Task.CompletedTask;
        public Task<AgentConfigurationOffer> GetConfigurationOfferAsync(AgentSettings s, long? r, CancellationToken token)
        { Revisions.Add(r); OnGet?.Invoke(r); if (GetError is not null) throw GetError; return Task.FromResult(Offer); }
        public Task SendConfigurationReceiptAsync(AgentSettings s, ConfigurationReceipt receipt, CancellationToken token)
        { if (SendError is not null) throw SendError; if (SendCallback is not null) return SendCallback(token); Sent.Add(receipt); return Task.CompletedTask; }
    }

    [Fact]
    public async Task AcceptanceFollowsDurableCacheAndRetrySurvivesRestart()
    {
        var id = Guid.NewGuid(); var api = new Api { Offer = new(1,id,false,Document(1),null), SendError = new HttpRequestException("offline") };
        var first = await Client(api).SynchronizeAsync(settings, DateTimeOffset.UtcNow);
        Assert.True(first.Succeeded); Assert.True(first.ReceiptPending); Assert.Equal(1, await Client(api).GetActiveRevisionAsync(settings));
        Assert.Single(Directory.GetFiles(CachePath + ".receipts", "*.json"));
        api.SendError = null; api.GetError = new HttpRequestException("offline configuration");
        var next = await Client(api).SynchronizeAsync(settings, DateTimeOffset.UtcNow);
        Assert.False(next.Succeeded); Assert.False(next.ReceiptPending);
        var receipt = Assert.Single(api.Sent); Assert.Equal(id, receipt.OfferId); Assert.Equal("Accepted", receipt.Status); Assert.Null(receipt.Reason);
        Assert.Empty(Directory.GetFiles(CachePath + ".receipts", "*.json"));
        Assert.Equal(1, await Client(api).GetActiveRevisionAsync(settings));
    }

    [Fact]
    public async Task RejectionTargetsOfferedRevisionAndPreservesPriorBytes()
    {
        var api = new Api { Offer = new(1,Guid.NewGuid(),false,Document(1),null) };
        Assert.True((await Client(api).SynchronizeAsync(settings, DateTimeOffset.UtcNow)).Succeeded);
        var old = await File.ReadAllBytesAsync(CachePath); var id = Guid.NewGuid();
        api.Offer = new(2,id,false,null,new JsonException("PRIVATE_PASSWORD"));
        var result = await Client(api).SynchronizeAsync(settings, DateTimeOffset.UtcNow);
        Assert.False(result.Succeeded); Assert.Equal(1,result.Revision);
        var rejected = api.Sent.Last(); Assert.Equal(2,rejected.Revision); Assert.Equal(id,rejected.OfferId);
        Assert.Equal("Rejected",rejected.Status); Assert.Equal("InvalidConfiguration",rejected.Reason);
        Assert.DoesNotContain("PRIVATE",JsonSerializer.Serialize(rejected));
        Assert.Equal(old,await File.ReadAllBytesAsync(CachePath));
    }

    [Fact]
    public async Task CacheWriteFailureCannotProduceAcceptance()
    {
        var api = new Api { Offer = new(1,Guid.NewGuid(),false,Document(1),null) };
        await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow); var old = await File.ReadAllBytesAsync(CachePath);
        api.Offer = new(2,Guid.NewGuid(),false,Document(2),null);
        api.OnGet = _ => { File.Move(CachePath,CachePath+".old"); Directory.CreateDirectory(CachePath); };
        try
        {
            var result = await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow);
            Assert.False(result.Succeeded); Assert.Equal(1,result.Revision);
            Assert.Equal("CacheWriteFailed",api.Sent.Last().Reason); Assert.Equal("Rejected",api.Sent.Last().Status);
        }
        finally { Directory.Delete(CachePath); File.Move(CachePath+".old",CachePath); }
        Assert.Equal(old,await File.ReadAllBytesAsync(CachePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyOrPreviousConnectionCacheNeedsFullResponseBeforeAcceptance(bool previousConnection)
    {
        var value=Document(1);
        var doc=value with {Jobs=value.Jobs.Select(j=>j with {AdditionalDailyTimes=[new(12,0)]}).ToArray()};
        await Cache.SaveAsync(new(settings.NormalizedServerAddress,doc,DateTimeOffset.UtcNow)
            {OfferId=previousConnection ? Guid.NewGuid() : null,ConfigurationHash=previousConnection ? AgentConfigurationCache.Digest(doc) : null});
        var id=Guid.NewGuid(); var api=new Api { Offer=new(1,id,true,null,null) };
        api.OnGet = revision => { if(revision is null) api.Offer=new(1,id,false,doc,null); };
        Assert.True((await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow)).Succeeded);
        Assert.Equal(new long?[]{1,null},api.Revisions); Assert.Equal("Accepted",Assert.Single(api.Sent).Status);
        Assert.Equal(id,(await Cache.LoadAsync())!.OfferId);
    }

    [Fact]
    public async Task FailedLegacyCacheUpgradeDoesNotRejectTheCachedRevision()
    {
        await Cache.SaveAsync(new(settings.NormalizedServerAddress,Document(1),DateTimeOffset.UtcNow));
        var api=new Api { Offer=new(1,Guid.NewGuid(),true,null,null) };
        api.OnGet = revision => { if(revision is null) throw new HttpRequestException("offline"); };
        Assert.False((await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow)).Succeeded);
        Assert.Empty(api.Sent); Assert.Equal(1,await Client(api).GetActiveRevisionAsync(settings));
    }

    [Fact]
    public async Task ModifiedCacheIsNotConfirmedByNotModified()
    {
        var api=new Api { Offer=new(1,Guid.NewGuid(),false,Document(1),null) };
        await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow);
        var json=await File.ReadAllTextAsync(CachePath); await File.WriteAllTextAsync(CachePath,json.Replace("Documents","Changed"));
        api.Offer=api.Offer with {NotModified=true,Configuration=null};
        var result=await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow);
        Assert.False(result.Succeeded); Assert.Single(api.Sent); Assert.Single(api.Revisions);
        await Assert.ThrowsAsync<InvalidDataException>(()=>Cache.LoadAsync());
    }

    [Fact]
    public async Task PendingReceiptNeverGoesToAnotherAgentOrServerOrStandalone()
    {
        var api=new Api {Offer=new(1,Guid.NewGuid(),false,Document(1),null),SendError=new HttpRequestException("offline")};
        await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow);
        api.SendError=null; api.GetError=new HttpRequestException("offline");
        await Client(api).SynchronizeAsync(settings with {ServerAddress="https://other.example.test/"},DateTimeOffset.UtcNow);
        await Client(api).SynchronizeAsync(settings with {AgentId=Guid.NewGuid()},DateTimeOffset.UtcNow);
        var requests=api.Revisions.Count;
        await Client(api).SynchronizeAsync(settings with {Mode=AgentMode.Standalone},DateTimeOffset.UtcNow);
        Assert.Empty(api.Sent); Assert.Equal(requests,api.Revisions.Count);
    }

    [Fact]
    public async Task BadPendingFileDoesNotBlockOtherReceiptsOrValidatedJobs()
    {
        var api=new Api {Offer=new(1,Guid.NewGuid(),false,Document(1),null),SendError=new HttpRequestException("offline")};
        await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow);
        var bad=Path.Combine(CachePath+".receipts",Guid.NewGuid()+".json"); await File.WriteAllTextAsync(bad,"invalid");
        api.SendError=null; api.Offer=new(1,api.Offer.OfferId,true,null,null);
        var result=await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow);
        Assert.True(result.Succeeded); Assert.True(result.ReceiptPending); Assert.Single(api.Sent); Assert.True(File.Exists(bad));
        Assert.Single(await Client(api).GetEffectiveJobsAsync(settings));
    }

    [Fact]
    public async Task MismatchedOfferAndDocumentCannotBeAccepted()
    {
        var api=new Api {Offer=new(3,Guid.NewGuid(),false,Document(2),null)};
        var result=await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow);
        Assert.False(result.Succeeded); Assert.False(File.Exists(CachePath));
        Assert.Equal(3,Assert.Single(api.Sent).Revision); Assert.Equal("Rejected",api.Sent[0].Status);
    }

    [Fact]
    public async Task StalledReceiptChannelHasABudgetAndKeepsValidatedJobs()
    {
        var api=new Api {Offer=new(1,Guid.NewGuid(),false,Document(1),null),SendCallback=token=>Task.Delay(Timeout.InfiniteTimeSpan,token)};
        var result=await Client(api).SynchronizeAsync(settings,DateTimeOffset.UtcNow).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Succeeded); Assert.True(result.ReceiptPending); Assert.Single(await Client(api).GetEffectiveJobsAsync(settings));
    }

    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
