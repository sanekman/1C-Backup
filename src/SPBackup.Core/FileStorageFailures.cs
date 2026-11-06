namespace SPBackup.Core;

internal static class FileStorageFailures
{
    // Match native codes, never localized exception messages. Credentials and capacity require user action.
    internal static int? Code(Exception error) => error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode
        : ((uint)error.HResult & 0xFFFF0000) == 0x80070000
        ? error.HResult & 0xFFFF : null;
    internal static bool Transient(Exception error) => error is IOException
        && Code(error) is 53 or 54 or 58 or 59 or 64 or 121 or 1222 or 1231 or 1232 or 1236 or 1237;

    internal static string? Hint(Exception error) => Code(error) switch
    {
        53 or 54 or 58 or 59 or 64 or 67 or 121 or 1222 or 1231 or 1232 or 1236 or 1237 =>
            "Не удалось завершить доставку в сетевую папку. Проверьте сеть или VPN, доступность сервера и путь вида \\\\server\\share\\backups. Для службы Windows используйте UNC-путь вместо буквы подключённого сетевого диска и проверьте доступ от пользователя службы.",
        5 => "Нет доступа к папке хранилища. Проверьте права записи, чтения и удаления для пользователя, который выполняет копирование. У службы Windows может быть другая учётная запись; доступ из Проводника другого пользователя не подтверждает доступ службы.",
        1219 => "Windows обнаружила подключения к одному серверу с разными учётными данными. Попросите администратора проверить существующие сетевые подключения пользователя агента или службы. Программа не отключает чужие подключения автоматически.",
        86 or 1326 or 1327 or 1330 or 1385 or 1909 => "Сетевое хранилище отклонило вход. Для NAS откройте задание → Хранилища → Отдельный логин и пароль NAS / сетевой папки и задайте сетевые учётные данные. Это может быть другой пароль, чем пароль пользователя службы Windows. Сохраните задание и проверьте хранилища через работающую службу. Доступ в Проводнике другого сеанса не подтверждает доступ службы; состояние учётной записи и разрешения проверяются на файловом сервере.",
        39 or 112 => "Во время записи закончилось место в хранилище. Освободите место или выберите другую папку. Программа не удаляет прежние копии заранее ради освобождения места; проверьте рабочую папку и результат каждого хранилища.",
        _ => null
    };
}

/// <summary>Filesystem seams for deterministic interrupted-transfer tests; production operations never overwrite.</summary>
internal sealed record FileStorageOperations
{
    internal Func<string, FileStream> OpenTemporary { get; init; } = path => new(path, FileMode.CreateNew,
        FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
    internal Action<string, string> Publish { get; init; } = (source, destination) => File.Move(source, destination, overwrite: false);
    internal Action<string> DeleteTemporary { get; init; } = File.Delete;
    internal Func<int, CancellationToken, Task> Delay { get; init; } = (retry, token) =>
        Task.Delay(TimeSpan.FromSeconds(retry == 1 ? 2 : 5), token);
}
