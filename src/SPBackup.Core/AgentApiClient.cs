using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPBackup.Core;

public interface IAgentCredentialProvider
{
    Task<string> GetBearerTokenAsync(string credentialKey, CancellationToken cancellationToken = default);
}

/// <summary>API v1, outgoing HTTPS only. Redirects and invalid TLS certificates are not accepted.</summary>
public sealed class AgentApiClient : IAgentApi, IExternalBackupReportApi, IDisposable
{
    private readonly HttpClient client;
    private readonly IAgentCredentialProvider credentials;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public AgentApiClient(IAgentCredentialProvider credentials, HttpMessageHandler? transport = null)
    {
        this.credentials = credentials;
        client = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    }

    private async Task<HttpRequestMessage> RequestAsync(AgentSettings settings, HttpMethod method, string endpoint,
        CancellationToken cancellationToken)
    {
        if (settings.Mode != AgentMode.Managed || settings.Validate().Count != 0)
            throw new ArgumentException("Для API требуется настроенный серверный режим.", nameof(settings));
        var token = await credentials.GetBearerTokenAsync(settings.CredentialKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Не заданы учётные данные агента.");
        var address = new Uri(new Uri(settings.NormalizedServerAddress), $"api/v1/agents/{settings.AgentId:D}/{endpoint}");
        var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add(AgentCapabilities.Header, AgentCapabilities.HeaderValue);
        return request;
    }

    public async Task<ServerConfiguration?> GetConfigurationAsync(AgentSettings settings, long? currentRevision,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var requestToken = timeout.Token;
        using var request = await RequestAsync(settings, HttpMethod.Get, "configuration", requestToken);
        if (currentRevision is not null) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{currentRevision}\""));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        response.EnsureSuccessStatusCode();
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Конфигурация сервера слишком велика.");
        await using var input = await response.Content.ReadAsStreamAsync(requestToken);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, requestToken)) != 0)
        {
            if (body.Length + read > limit) throw new InvalidDataException("Конфигурация сервера слишком велика.");
            body.Write(buffer, 0, read);
        }
        var configuration = JsonSerializer.Deserialize<ServerConfiguration>(body.ToArray(), JsonOptions)
            ?? throw new InvalidDataException("Сервер вернул пустую конфигурацию.");
        AgentConfigurationCache.Validate(configuration);
        if (configuration.AgentId != settings.AgentId) throw new InvalidDataException("Сервер вернул настройки другого агента.");
        return configuration;
    }

    public async Task SendHeartbeatAsync(AgentSettings settings, AgentHeartbeat heartbeat,
        CancellationToken cancellationToken = default)
    {
        if (heartbeat.AgentId != settings.AgentId) throw new ArgumentException("Неверный идентификатор heartbeat.", nameof(heartbeat));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = await RequestAsync(settings, HttpMethod.Post, "heartbeat", timeout.Token);
        request.Content = JsonContent.Create(heartbeat, options: JsonOptions);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => client.Dispose();

    public async Task SendExternalBackupAsync(AgentSettings settings, ExternalBackupReport report,
        CancellationToken cancellationToken = default)
    {
        if (report.AgentId != settings.AgentId || report.TargetServer != settings.NormalizedServerAddress ||
            report.ReportId == Guid.Empty || report.MonitorId == Guid.Empty)
            throw new ArgumentException("Отчёт не относится к этому агенту или серверу.", nameof(report));
        report.Observation.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = await RequestAsync(settings, HttpMethod.Post, "external-backups", timeout.Token);
        request.Headers.Add("Idempotency-Key", report.ReportId.ToString("D"));
        request.Content = JsonContent.Create(report with { DeliveredUtc = null }, options: JsonOptions);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
    }
}
