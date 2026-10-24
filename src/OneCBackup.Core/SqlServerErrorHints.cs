using System.ComponentModel;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;

namespace OneCBackup.Core;

/// <summary>Stable provider/native codes only: localized driver messages can contain connection details.</summary>
internal static class SqlServerErrorHints
{
    internal static string? Describe(Exception error)
    {
        if (error is OperationCanceledException) return null;
        var errors = new List<Exception>();
        var queue = new Queue<Exception>(); queue.Enqueue(error);
        while (queue.Count != 0 && errors.Count < 16)
        {
            var item = queue.Dequeue();
            if (errors.Contains(item)) continue;
            errors.Add(item);
            if (item is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Take(16)) queue.Enqueue(inner);
            else if (item.InnerException is { } inner) queue.Enqueue(inner);
        }
        var numbers = errors.OfType<SqlException>().SelectMany(sql => sql.Errors.Cast<SqlError>().Select(item => item.Number)).ToArray();
        var native = errors.SelectMany(item => item is Win32Exception win
            ? new[] { item.HResult, win.NativeErrorCode } : new[] { item.HResult }).Concat(numbers).ToHashSet();
        // Certificate errors take priority over the generic provider/network error that often wraps them.
        foreach (var (code, hint) in CertificateErrors)
            if (native.Contains(code)) return hint + $" Код сертификата: 0x{unchecked((uint)code):X8}.";
        if (native.Contains(unchecked((int)0x80090322)))
            return "Не подтверждено имя сервера MS SQL. Попросите администратора проверить имя подключения, сертификат и настройки аутентификации сервера. Проверка сертификата остаётся включённой. Код Windows: 0x80090322.";
        if (errors.Any(item => item is AuthenticationException))
            return "Не удалось подтвердить подлинность подключения к MS SQL. Попросите администратора проверить имя сервера, способ входа, сертификат и настройки TLS. Проверка сертификата остаётся включённой.";
        foreach (var code in new[] { 4060, 18487, 18488, 18456, 18452, 18470, 229, -2, 258, 26, 40, 53, 64, 233, 10060, 10061, 10054, 11001 })
        {
            if (!numbers.Contains(code)) continue;
            var hint = code switch
            {
                4060 => "MS SQL не разрешил открыть базу. Проверьте имя базы и права выбранного пользователя с администратором. Подключению для копирования нужен доступ к master, внешнему мониторингу — к msdb.",
                18487 or 18488 => "Пароль SQL-логина истёк или сервер требует его сменить. Попросите администратора изменить пароль на MS SQL, затем задайте новый пароль в задании или источнике мониторинга и сохраните настройки.",
                18456 or 18452 or 18470 => "MS SQL отклонил вход. Откройте настройки подключения: проверьте способ входа Windows или SQL-логин, заново задайте логин и пароль при необходимости. Попросите администратора проверить, что этот вход разрешён. При Windows-входе служба использует свою учётную запись.",
                229 => "Недостаточно прав MS SQL. Попросите администратора проверить права выбранного пользователя: копирование и проверка копии требуют своих разрешений, внешний мониторинг — чтения истории msdb. Пароль менять не требуется, если вход уже работает.",
                -2 or 258 => "MS SQL не ответил за отведённое время. Проверьте доступность сервера и его нагрузку. Если подключение работает, но копирование длительное, проверьте ограничение времени в задании.",
                64 or 233 or 10054 => "Соединение с MS SQL прервано. Проверьте сеть и состояние сервера; попросите администратора проверить настройки подключения и TLS. Повторите проверку источника после восстановления связи.",
                _ => "Не удалось связаться с MS SQL. Проверьте имя сервера и экземпляра, порт, сеть или VPN. Попросите администратора проверить, что MS SQL запущен и подключение разрешено межсетевым экраном. Затем повторите проверку источника."
            };
            return hint + $" Код MS SQL: {code}.";
        }
        return null; // Unrecognized failures retain their existing explanation; do not guess from text.
    }

    private static readonly (int Code, string Hint)[] CertificateErrors =
    [
        (unchecked((int)0x800B0109), "Сертификат MS SQL выдан недоверенным центром. Попросите администратора настроить доверенную цепочку сертификатов на компьютере агента, включая работу службы. Не отключайте проверку сертификата."),
        (unchecked((int)0x800B010A), "Не удалось построить цепочку сертификата MS SQL. Попросите администратора проверить промежуточные сертификаты и доверенный корневой сертификат на компьютере агента. Проверка сертификата остаётся включённой."),
        (unchecked((int)0x800B010C), "Сертификат MS SQL отозван. Попросите администратора заменить его действующим сертификатом. Проверка сертификата остаётся включённой."),
        (unchecked((int)0x80090325), "Не подтверждено доверие к сертификату MS SQL. Попросите администратора проверить цепочку сертификатов на компьютере агента и для службы. Проверка сертификата остаётся включённой."),
        (unchecked((int)0x800B010F), "Имя в сертификате MS SQL не совпадает с адресом подключения. Используйте согласованное с администратором имя сервера из сертификата либо исправьте сертификат. Проверка сертификата остаётся включённой."),
        (unchecked((int)0x800B0101), "Сертификат MS SQL ещё не действует или его срок истёк. Проверьте дату и время компьютеров; попросите администратора обновить сертификат при необходимости. Проверка сертификата остаётся включённой."),
        (unchecked((int)0x80090328), "Истёк срок сертификата MS SQL. Проверьте дату и время компьютеров и попросите администратора обновить сертификат. Проверка сертификата остаётся включённой.")
    ];
}
