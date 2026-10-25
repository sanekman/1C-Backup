using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class AgentApiClientTests
{
    private readonly AgentSettings settings = new() { Mode = AgentMode.Managed,
        ServerAddress = "https://backup.example.com/prefix/", CredentialKey = "fixture-binding" };
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task RequestsVersionedConfigurationWithTokenAndEtag()
    {
        var configuration = new ServerConfiguration(settings.AgentId, 2,
            [new BackupJob { Name = "test", Source = "database", DestinationDirectory = "D:\\Backups" }]);
        var credential = new FixtureCredentials();
        using var client = new AgentApiClient(credential, new Handler(request =>
        {
            Assert.Equal($"https://backup.example.com/prefix/api/v1/agents/{settings.AgentId:D}/configuration", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization.Parameter);
            Assert.Equal("\"1\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(configuration, WireOptions), Encoding.UTF8, "application/json") };
        }));
        var result = await client.GetConfigurationAsync(settings, 1);
        Assert.Equal(configuration.AgentId, result!.AgentId);
        Assert.Equal(2, result.Revision);
        Assert.Equal(configuration.Jobs[0], Assert.Single(result.Jobs));
        Assert.Equal(settings.CredentialKey, credential.Key);
    }

    [Fact]
    public async Task HandlesNotModifiedWithoutParsingBody()
    {
        using var client = new AgentApiClient(new FixtureCredentials(), new Handler(_ => new(HttpStatusCode.NotModified)));
        Assert.Null(await client.GetConfigurationAsync(settings, 1));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task RejectsUnsuccessfulResponses(HttpStatusCode status)
    {
        var requests = 0;
        using var client = new AgentApiClient(new FixtureCredentials(), new Handler(_ =>
        { requests++; return new(status) { Headers = { Location = new Uri("http://other.example.com/") } }; }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetConfigurationAsync(settings, null));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task StandaloneDoesNotReadCredentialsOrSendHttp()
    {
        var credentials = new FixtureCredentials();
        using var client = new AgentApiClient(credentials, new Handler(_ => throw new InvalidOperationException("must not send")));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetConfigurationAsync(settings with { Mode = AgentMode.Standalone }, null));
        Assert.Null(credentials.Key);
    }

    [Fact]
    public async Task RejectsMissingCredentialsBeforeHttpRequest()
    {
        using var client = new AgentApiClient(new FixtureCredentials { Token = "" }, new Handler(_ => throw new InvalidOperationException("must not send")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetConfigurationAsync(settings, null));
    }

    [Fact]
    public async Task RejectsAnotherAgentsConfiguration()
    {
        var configuration = new ServerConfiguration(Guid.NewGuid(), 1, []);
        using var client = new AgentApiClient(new FixtureCredentials(), JsonHandler(JsonSerializer.Serialize(configuration, WireOptions)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetConfigurationAsync(settings, null));
    }

    [Fact]
    public async Task RejectsUnsupportedProtocol()
    {
        var configuration = new ServerConfiguration(settings.AgentId, 1, [], 2);
        using var client = new AgentApiClient(new FixtureCredentials(), JsonHandler(JsonSerializer.Serialize(configuration, WireOptions)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetConfigurationAsync(settings, null));
    }

    [Fact]
    public async Task RequiresExplicitServerJobIdentifier()
    {
        var json = $"{{\"agentId\":\"{settings.AgentId}\",\"revision\":1,\"jobs\":[{{\"name\":\"db\",\"source\":\"db\",\"destinationDirectory\":\"/backups\"}}]}}";
        using var client = new AgentApiClient(new FixtureCredentials(), JsonHandler(json));
        await Assert.ThrowsAsync<JsonException>(() => client.GetConfigurationAsync(settings, null));
    }

    [Fact]
    public async Task RejectsOversizedConfiguration()
    {
        using var client = new AgentApiClient(new FixtureCredentials(), new Handler(_ => new(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[4 * 1024 * 1024 + 1]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetConfigurationAsync(settings, null));
    }

    [Fact]
    public async Task EnforcesBodyLimitWhenContentLengthIsAbsent()
    {
        using var client = new AgentApiClient(new FixtureCredentials(), new Handler(_ => new(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(new byte[4 * 1024 * 1024 + 1])) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetConfigurationAsync(settings, null));
    }

    [Fact]
    public async Task CallerCanCancelPendingHttpRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new AgentApiClient(new FixtureCredentials(), new PendingHandler(entered));
        using var stop = new CancellationTokenSource();
        var request = client.GetConfigurationAsync(settings, null, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task HeartbeatDescribesActualCapabilityWithoutSendingTokenInBody()
    {
        using var client = new AgentApiClient(new FixtureCredentials(), new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/heartbeat", request.RequestUri!.AbsolutePath);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(body);
            Assert.False(document.RootElement.GetProperty("backupExecutionAvailable").GetBoolean());
            Assert.Equal(2, document.RootElement.GetProperty("configurationRevision").GetInt64());
            Assert.DoesNotContain("fixture-token", body);
            return new(HttpStatusCode.NoContent);
        }));
        await client.SendHeartbeatAsync(settings, new(settings.AgentId, "0.1-test", false, 2, DateTimeOffset.UtcNow));
    }

    private static Handler JsonHandler(string json) => new(_ => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }

    private sealed class FixtureCredentials : IAgentCredentialProvider
    {
        public string Token { get; init; } = "fixture-token";
        public string? Key { get; private set; }
        public Task<string> GetBearerTokenAsync(string credentialKey, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Key = credentialKey; return Task.FromResult(Token); }
    }

    private sealed class PendingHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.SetResult();
            return new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class NonSeekableStream(byte[] contents) : Stream
    {
        private readonly MemoryStream inner = new(contents);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
