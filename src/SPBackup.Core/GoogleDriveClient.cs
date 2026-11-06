using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SPBackup.Core;

/// <summary>Fixed Google HTTPS endpoints; credentials never appear in locations or diagnostics.</summary>
public sealed class GoogleDriveClient(HttpClient http, GoogleDriveCredentials credentials)
{
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expires;
    internal static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        DiagnosticLog.Current.Write(DiagnosticEvent.GoogleDriveRequestFailed, DiagnosticLevel.Warning,
            storageKind: StorageKind.GoogleDrive, values: new(Code: code));
        throw new IOException(code switch
        {
            400 or 401 => "Google Drive: подключение истекло или отозвано. Подключите аккаунт заново.",
            403 => "Google Drive: нет доступа к папке или достигнут лимит аккаунта. Проверьте права и свободное место.",
            404 => "Google Drive: файл или папка недоступны.",
            429 => "Google Drive: превышен лимит запросов. Повторите позже.",
            _ => "Google Drive не выполнил запрос (HTTP " + code + ")."
        });
    }
    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > 65536) throw new InvalidDataException("Слишком большой ответ Google Drive.");
            output.Write(buffer, 0, count);
        }
        try { return JsonDocument.Parse(output.ToArray()); }
        catch (JsonException) { throw new InvalidDataException("Некорректный ответ Google Drive."); }
    }
    private async Task<string> TokenAsync(CancellationToken token)
    {
        await tokenGate.WaitAsync(token);
        try
        {
            if (accessToken is not null && expires > DateTimeOffset.UtcNow.AddMinutes(1)) return accessToken;
            credentials.Validate();
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = credentials.ClientId, ["client_secret"] = credentials.ClientSecret,
                ["refresh_token"] = credentials.RefreshToken, ["grant_type"] = "refresh_token" }), token);
            using var json = await ReadJsonAsync(response, token);
            var root = json.RootElement;
            accessToken = root.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192 || accessToken.Any(char.IsControl))
                throw new InvalidDataException("Некорректный ответ авторизации Google.");
            expires = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32());
            return accessToken;
        }
        finally { tokenGate.Release(); }
    }
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken token, long? uploadLength = null)
    {
        var uri = path.StartsWith("https://", StringComparison.Ordinal) ? new Uri(path) : new Uri("https://www.googleapis.com/" + path);
        if (uri.Scheme != "https" || uri.Host != "www.googleapis.com" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Некорректный адрес Google Drive.");
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (uploadLength is long length)
        {
            request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            request.Headers.Add("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(token));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
    public async Task<JsonDocument> JsonAsync(HttpMethod method, string path, object? data, CancellationToken token)
    {
        using var response = await SendAsync(method, path, data is null ? null : new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json"), token);
        return await ReadJsonAsync(response, token);
    }
    public async Task CheckFolderAsync(string id, CancellationToken token)
    {
        if (!GoogleDriveSettings.IsId(id)) throw new InvalidDataException("Некорректная папка Google Drive.");
        using var json = await JsonAsync(HttpMethod.Get, "drive/v3/files/" + id + "?fields=id,mimeType,trashed,capabilities(canAddChildren)&supportsAllDrives=true", null, token);
        var r = json.RootElement;
        if (r.GetProperty("mimeType").GetString() != "application/vnd.google-apps.folder" || r.GetProperty("trashed").GetBoolean()
            || !r.GetProperty("capabilities").GetProperty("canAddChildren").GetBoolean())
            throw new InvalidDataException("Папка Google Drive недоступна для записи.");
    }
    public async Task<string> CreateFolderAsync(CancellationToken token)
    {
        using var json = await JsonAsync(HttpMethod.Post, "drive/v3/files?fields=id", new
        { name = "SP Backup", mimeType = "application/vnd.google-apps.folder" }, token);
        var id = json.RootElement.GetProperty("id").GetString();
        if (!GoogleDriveSettings.IsId(id)) throw new InvalidDataException("Google Drive не создал папку.");
        return id!;
    }
}
