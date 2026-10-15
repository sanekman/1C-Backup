using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OneCBackup.Core;

/// <summary>Desktop OAuth: loopback IPv4 listener, state, PKCE, offline access and drive.file only.</summary>
public static class GoogleDriveAuthorization
{
    public sealed record DesktopClient(string ClientId, string ClientSecret);
    public static DesktopClient ParseDesktopClient(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var installed = doc.RootElement.GetProperty("installed");
            var client = new DesktopClient(installed.GetProperty("client_id").GetString()!, installed.GetProperty("client_secret").GetString()!);
            new GoogleDriveCredentials(client.ClientId, client.ClientSecret, "validation").Validate();
            return client;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        { throw new InvalidDataException("Выберите JSON OAuth-клиента Google типа Desktop app, а не служебного аккаунта."); }
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static async Task<GoogleDriveCredentials> ConnectAsync(DesktopClient client, HttpClient http,
        Action<string> openBrowser, CancellationToken cancellationToken)
    {
        new GoogleDriveCredentials(client.ClientId, client.ClientSecret, "validation").Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            var parameters = new Dictionary<string, string>
            { ["client_id"] = client.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
                ["scope"] = "https://www.googleapis.com/auth/drive.file", ["access_type"] = "offline", ["prompt"] = "consent",
                ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256" };
            openBrowser("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
            string? code = null;
            while (code is null)
            {
                using var connection = await listener.AcceptTcpClientAsync(token);
                await using var stream = connection.GetStream();
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var bytes = new byte[16384]; var used = 0; var ended = false;
                while (used < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(used), requestTimeout.Token);
                    if (read == 0) break;
                    used += read;
                    if (Encoding.ASCII.GetString(bytes, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) { ended = true; break; }
                }
                var line = Encoding.ASCII.GetString(bytes, 0, used).Split("\r\n")[0];
                var parts = line.Split(' ');
                var valid = ended && parts.Length == 3 && parts[0] == "GET" && parts[1].StartsWith("/?", StringComparison.Ordinal);
                Dictionary<string, string> query = new();
                if (valid)
                {
                    foreach (var pair in parts[1][2..].Split('&'))
                    {
                        var item = pair.Split('=', 2); if (item.Length != 2) { valid = false; break; }
                        if (!query.TryAdd(Uri.UnescapeDataString(item[0]), Uri.UnescapeDataString(item[1].Replace('+', ' ')))) { valid = false; break; }
                    }
                    valid = valid && query.TryGetValue("state", out var returnedState) && returnedState == state;
                }
                var body = Encoding.UTF8.GetBytes(valid ? "Ответ получен. Вернитесь в 1C-Backup." : "Запрос не принят.");
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (valid ? "200 OK" : "400 Bad Request") + "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
                await stream.WriteAsync(header, token); await stream.WriteAsync(body, token);
                if (!valid) continue;
                if (query.ContainsKey("error")) throw new InvalidOperationException("Вход Google отменён или доступ не предоставлен.");
                if (!query.TryGetValue("code", out code) || string.IsNullOrWhiteSpace(code) || code.Length > 8192)
                    throw new InvalidDataException("Google не вернул код авторизации.");
            }
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret, ["grant_type"] = "authorization_code",
                ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier }), token);
            using var result = await GoogleDriveClient.ReadJsonAsync(response, token);
            if (!result.RootElement.TryGetProperty("refresh_token", out var refresh))
                throw new InvalidDataException("Google не предоставил фоновый доступ. Подключите аккаунт заново с подтверждением разрешения.");
            var credentials = new GoogleDriveCredentials(client.ClientId, client.ClientSecret, refresh.GetString()!);
            credentials.Validate(); return credentials;
        }
        finally { listener.Stop(); }
    }
}
