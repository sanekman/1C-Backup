using System.Net;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class AgentConnectionTests
{
    private readonly ServerMonitoringSettings connection = new("https://backup.example.test/prefix/", Guid.NewGuid(), "enrollment-key");
    private AgentSettings Settings => new() { AgentId = connection.AgentId, Mode = AgentMode.Managed,
        ServerAddress = connection.Address, CredentialKey = connection.CredentialKey };

    private sealed class Store : IFtpCredentialStore
    {
        public string Value = "test-token";
        public Exception? Error;
        public readonly List<string> Reads = [];
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Reads.Add(key);
            if (Error is not null) throw Error;
            return Task.FromResult(new NetworkCredential("agent", Value));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request));
    }

    [Theory]
    [InlineData("address")]
    [InlineData("agent")]
    [InlineData("key")]
    [InlineData("prefix")]
    public async Task ChangedBindingCannotReadSecretOrSendAnyApiRequest(string fault)
    {
        var settings = fault switch
        {
            "address" => Settings with { ServerAddress = "https://another.example.test/prefix/" },
            "agent" => Settings with { AgentId = Guid.NewGuid() },
            "key" => Settings with { CredentialKey = "different-key" },
            _ => Settings with { ServerAddress = "https://backup.example.test/other-prefix/" }
        };
        var store = new Store();
        using var api = new AgentApiClient(connection, store, new Handler(_ => throw new InvalidOperationException("Must not send")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetConfigurationAsync(settings, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.SendHeartbeatAsync(settings, new(settings.AgentId, "test", false, null, DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.SendConfigurationReceiptAsync(settings,
            new(settings.AgentId, 1, Guid.NewGuid(), "Accepted", null, DateTimeOffset.UtcNow)));
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task ReadsDelegatedStoreAgainAfterTokenRotationWithoutAdvertisingManagedExecution()
    {
        var store = new Store(); var received = new List<string>();
        using var api = new AgentApiClient(connection, store, new Handler(request =>
        {
            Assert.Equal($"{connection.Address}api/v1/agents/{connection.AgentId:D}/configuration", request.RequestUri!.AbsoluteUri);
            received.Add(request.Headers.Authorization!.Parameter!);
            Assert.DoesNotContain(AgentCapabilities.ManagedExecutionV1,
                AgentCapabilities.Parse(string.Join(",", request.Headers.GetValues(AgentCapabilities.Header))));
            return new(HttpStatusCode.NotModified);
        }));
        var settings = Settings with { ServerAddress = "https://BACKUP.example.test:443/prefix" };
        Assert.Null(await api.GetConfigurationAsync(settings, 1));
        store.Value = "rotated-token._~+/==";
        Assert.Null(await api.GetConfigurationAsync(settings, 1));
        Assert.Equal(new[] { "test-token", store.Value }, received);
        Assert.Equal(new[] { connection.CredentialKey, connection.CredentialKey }, store.Reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("token secret")]
    [InlineData("token\r\nInjected: secret")]
    [InlineData("тестовый_секрет_не_ASCII")]
    [InlineData("===")]
    [InlineData("token=next")]
    public async Task MalformedStoredTokenNeverReachesHttpAndIsNotIncludedInError(string token)
    {
        var store = new Store { Value = token };
        using var api = new AgentApiClient(connection, store, new Handler(_ => throw new InvalidOperationException("Must not send")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetConfigurationAsync(Settings, null));
        Assert.Null(error.InnerException);
        if (token.Length > 1) Assert.DoesNotContain(token, error.ToString());
        Assert.Single(store.Reads);
    }

    [Fact]
    public async Task CredentialStoreFailureDoesNotExposeItsExceptionOrSecret()
    {
        const string secret = "private-store-marker";
        var store = new Store { Error = new IOException("Cannot read password=" + secret) };
        var sent = false;
        using var api = new AgentApiClient(connection, store, new Handler(_ => { sent = true; return new(HttpStatusCode.NotModified); }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetConfigurationAsync(Settings, null));
        Assert.Null(error.InnerException); Assert.DoesNotContain(secret, error.ToString());
        Assert.False(sent);
        store.Error = null;
        Assert.Null(await api.GetConfigurationAsync(Settings, 1)); Assert.True(sent);
    }

    [Fact]
    public async Task CancelledRequestDoesNotReadCredentialStore()
    {
        var store = new Store(); using var stop = new CancellationTokenSource(); stop.Cancel();
        using var api = new AgentApiClient(connection, store, new Handler(_ => throw new InvalidOperationException("Must not send")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetConfigurationAsync(Settings, null, stop.Token));
        Assert.Empty(store.Reads);
    }

    private sealed class CancellingStore(CancellationTokenSource stop) : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
        {
            stop.Cancel();
            throw new OperationCanceledException("password=private-cancel-marker", token);
        }
    }

    [Fact]
    public async Task StoreCancellationPreservesCancellationWithoutExposingAdapterDetails()
    {
        using var stop = new CancellationTokenSource();
        using var api = new AgentApiClient(connection, new CancellingStore(stop),
            new Handler(_ => throw new InvalidOperationException("Must not send")));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetConfigurationAsync(Settings, null, stop.Token));
        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.Null(error.InnerException); Assert.DoesNotContain("private-cancel-marker", error.ToString());
    }

    [Theory]
    [InlineData("certificate")]
    [InlineData("null-certificate")]
    [InlineData("long-key")]
    [InlineData("control-key")]
    public void InvalidConnectionIsRejectedBeforeReadingSecrets(string fault)
    {
        var invalid = fault switch
        {
            "certificate" => connection with { CertificateSha256 = "untrusted" },
            "null-certificate" => connection with { CertificateSha256 = null! },
            "long-key" => connection with { CredentialKey = new string('a', 513) },
            _ => connection with { CredentialKey = "key\n" }
        };
        var store = new Store();
        Assert.Throws<InvalidDataException>(() => new AgentApiClient(invalid, store));
        Assert.Empty(store.Reads);
    }
}
