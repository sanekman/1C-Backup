using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SPBackup.Server;

public sealed record ServerUpdateState(string Phase, string? CurrentVersion = null, string? LatestVersion = null,
    double? CheckedUtc = null, string? JobId = null, string? TargetVersion = null, double? StartedUtc = null,
    double? FinishedUtc = null, string? BackupDirectory = null, string? RequestError = null, ServerUpdateResult? LastResult = null)
{
    public bool Busy => Phase is "checking" or "queued" or "updating";
    public bool CanStart => Phase == "available" && ServerUpdates.ValidVersion(LatestVersion) && CheckedUtc is { } checkedUtc
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - checkedUtc is >= 0 and <= 1800;
    public string Message => Phase switch
    {
        "idle" => "Проверьте, доступна ли новая версия сервера.",
        "checking" => "Проверяем опубликованные версии…",
        "available" => $"Доступна версия {LatestVersion}.",
        "upToDate" => "Установлена актуальная версия сервера.",
        "queued" or "updating" => "Обновление выполняется. Панель может временно потерять связь с сервером.",
        "updated" => "Сервер обновлён. Проверка готовности прошла.",
        "rolledBack" => "Обновление не выполнено. Прежняя программа и данные восстановлены.",
        "recoveryRequired" => "Возврат не завершён. Нужна проверка системного администратора; сохранён комплект возврата.",
        "interrupted" => "Выполнение системной задачи прервано. Результат не подтверждён; нужна проверка администратора.",
        "checkFailed" => "Не удалось проверить новую версию. Проверьте доступ сервера к GitHub и повторите проверку.",
        "dispatchFailed" => "Не удалось запустить системную задачу. Проверьте службу обновления.",
        "failed" => "Обновление не выполнено. Системный администратор может проверить журнал службы обновления.",
        "notConfigured" => "Обновление из панели ещё не подключено.",
        "unsupported" => "Для этой установки используйте штатный установщик сервера.",
        _ => "Служба обновления сейчас недоступна. Результат обновления не подтверждён."
    };
}

public sealed record ServerUpdateResult(string Phase, string? JobId = null, string? TargetVersion = null, double? FinishedUtc = null, string? BackupDirectory = null)
{ public string Message => new ServerUpdateState(Phase).Message; }

public sealed class ServerUpdates(ServerOptions settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string BuildVersion => typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "unknown";
    public static bool ValidVersion(string? version) => version is not null && Regex.IsMatch(version, @"\A0\.1\.0-preview\.[1-9][0-9]*\z", RegexOptions.CultureInvariant);
    public Task<ServerUpdateState> ReadAsync(CancellationToken token = default) => RequestAsync("status", null, token);
    public Task<ServerUpdateState> CheckAsync(CancellationToken token = default) => RequestAsync("check", null, token);
    public Task<ServerUpdateState> StartAsync(string? version, CancellationToken token = default)
    {
        if (!ValidVersion(version)) throw new ServerRequestException(400, "invalid_server_version");
        return RequestAsync("start", version, token);
    }
    private static bool ValidTime(double? value) => value is null || double.IsFinite(value.Value) && value.Value is >= 0 and <= 253402300799;
    private async Task<ServerUpdateState> RequestAsync(string command, string? version, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return new("unsupported", BuildVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(settings.UpdateControlSocketPath), timeout.Token);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = version is null ? JsonSerializer.Serialize(new { command }, Json) : JsonSerializer.Serialize(new { command, version }, Json);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"), timeout.Token);
            var response = new List<byte>(); var buffer = new byte[1];
            while (response.Count <= 16384)
            {
                if (await stream.ReadAsync(buffer, timeout.Token) == 0) throw new IOException();
                if (buffer[0] == (byte)'\n')
                {
                    var state = JsonSerializer.Deserialize<ServerUpdateState>(response.ToArray(), Json) ?? throw new JsonException();
                    if (state.Phase is not ("idle" or "checking" or "available" or "upToDate" or "queued" or "updating" or "updated"
                        or "rolledBack" or "recoveryRequired" or "interrupted" or "checkFailed" or "dispatchFailed" or "failed")) throw new JsonException();
                    if (!ValidVersion(state.CurrentVersion) || state.LatestVersion is { } latest && !ValidVersion(latest)
                        || state.TargetVersion is { } target && !ValidVersion(target)) throw new JsonException();
                    if (state.LastResult is { } last && (last.Phase is not ("updated" or "rolledBack" or "recoveryRequired" or "failed" or "interrupted")
                        || !ValidVersion(last.TargetVersion))) throw new JsonException();
                    if (!ValidTime(state.CheckedUtc) || !ValidTime(state.StartedUtc) || !ValidTime(state.FinishedUtc)
                        || !ValidTime(state.LastResult?.FinishedUtc)) throw new JsonException();
                    return state;
                }
                response.Add(buffer[0]);
            }
            throw new JsonException();
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.ConnectionRefused
            || ex.NativeErrorCode == 2) { return new("notConfigured", BuildVersion); }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException or OperationCanceledException)
        { return new("unavailable", BuildVersion); }
    }
}
