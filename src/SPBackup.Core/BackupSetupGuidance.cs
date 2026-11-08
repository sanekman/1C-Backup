namespace SPBackup.Core;

/// <summary>Short setup examples and a first-run procedure. It performs no I/O or configuration changes.</summary>
public static class BackupSetupGuidance
{
    public static string SourceExample(DatabaseKind kind) => kind switch
    {
        DatabaseKind.OneCFile => @"Например: C:\Bases\Accounting или \\SERVER\Bases\Accounting. Нужна папка с 1Cv8.1CD.",
        DatabaseKind.FilesAndFolders => @"Пример: D:\Documents или \\NAS\Documents. Папку архивов выберите на следующем шаге.",
        DatabaseKind.SqlServer => "Например: Accounting — точное имя базы из SSMS. Адрес MS SQL укажите ниже; сюда не нужны путь .bak или строка подключения.",
        DatabaseKind.PostgreSql => "Например: accounting — точное имя базы из pgAdmin. Сервер, порт и пользователь задаются отдельно ниже; сюда не нужен адрес postgresql:// или путь дампа.",
        DatabaseKind.WebDav => "Например: https://cloud.example.ru/remote.php/dav/files/user/Documents/. Нужен конечный WebDAV-адрес папки, а не ссылка на страницу облака в браузере.",
        _ => "Выберите тип источника."
    };

    public static string? DatabaseNameError(DatabaseKind kind, string source)
    {
        if (kind is not (DatabaseKind.SqlServer or DatabaseKind.PostgreSql)) return null;
        var value = source.Trim();
        if (value.Contains("://", StringComparison.Ordinal) || value.StartsWith(@"\\", StringComparison.Ordinal)
            || value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/'
            || value.StartsWith('/') || value.Contains(';') && value.Contains('='))
            return "В поле «Имя базы данных» укажите только точное имя базы, например Accounting. Адрес сервера и способ входа задаются ниже. Путь к архиву и строка подключения здесь не нужны.";
        return null;
    }

    public static string? DatabaseServerError(DatabaseKind kind, string server)
    {
        if (kind is not (DatabaseKind.SqlServer or DatabaseKind.PostgreSql)) return null;
        var value = server.Trim();
        if (value.Contains("://", StringComparison.Ordinal) || value.Contains(';') && value.Contains('='))
            return "В поле сервера укажите имя компьютера или IP без https:// и строки подключения. Имя базы задаётся отдельно выше.";
        if (kind == DatabaseKind.PostgreSql && value.Contains(':')
            && !System.Net.IPAddress.TryParse(value.Trim('[', ']'), out _))
            return "Порт PostgreSQL укажите в отдельном поле «Порт», например 5432. В поле сервера оставьте имя компьютера или IP.";
        return null;
    }

    public static string FirstRun(DatabaseKind kind) => "1. Сохраните задание\n"
        + "Для первой проверки выберите «Только вручную». Сохранение не подтверждает доступ и не создаёт копию. Выберите сохранённое задание в списке.\n\n"
        + "2. Проверьте источник и хранилища\n"
        + "Если копии будет делать служба, сначала включите её в «Настройки → Фоновая работа». Нажмите «Проверить источник», затем «Проверить хранилища». В отчётах проверьте пользователя исполнителя. Доступ из Проводника не подтверждает доступ службы. Старый агент без поддержки проверок нужно обновить и перезапустить.\n"
        + (kind is DatabaseKind.SqlServer ? "MS SQL: вход и BACKUP DATABASE не подтверждают доступ SQL-службы к папке .bak и возможность восстановления.\n"
            : kind is DatabaseKind.PostgreSql ? "PostgreSQL: SELECT 1 подтверждает подключение; права чтения всех таблиц, pg_dump и восстановление проверяются отдельно. Внешние настройки входа должны существовать у пользователя службы.\n"
            : kind is DatabaseKind.WebDav ? "WebDAV: список папки не подтверждает скачивание всех файлов. Используйте пароль приложения облачного сервиса.\n"
            : "Для сетевого источника нужны права чтения у пользователя исполнителя. Отдельный логин NAS в хранилище применяется к назначению, а не к источнику.\n")
        + "\n3. Создайте пробную копию\nНажмите «Создать копию». При работающей службе запрос передаётся ей. Дождитесь итога в «Истории запусков»; при предупреждении прочитайте пропуски и причины. Не запускайте повторно, пока первый запрос принят или выполняется.\n\n"
        + "4. Найдите и проверьте архив\nНажмите «Последняя копия», проверьте нужное задание, время, хранилище и результат. Выполните «Проверить последние копии»; если кнопка требует остановки исполнителя, сначала дождитесь завершения всех операций. Проверка SHA-256 не заменяет восстановление.\n\n"
        + "5. Проверьте восстановление, затем включите расписание\n"
        + (kind is DatabaseKind.SqlServer or DatabaseKind.PostgreSql
            ? "Сохраните выбранную копию. С администратором СУБД восстановите её в отдельную тестовую базу с другим именем. Проверьте таблицы и данные; рабочую базу не заменяйте."
            : kind is DatabaseKind.OneCFile
                ? "Сохраните выбранную копию. ZIP распакуйте в отдельную пустую папку и откройте тестовую базу в 1С; .dt загрузите в отдельную тестовую базу. Проверьте документы. Рабочую базу не заменяйте."
                : "Используйте восстановление выбранной копии в отдельную пустую папку. Для инкремента нужна вся цепочка. Сравните несколько файлов, подпапок и содержимое с оригиналом; исходные данные не заменяйте.")
        + "\nПосле успешного восстановления настройте расписание задания и включите автоматическое копирование. Проверьте ближайший запуск в истории. При ошибке исправьте параметры и повторите проверку; одно успешное подключение не гарантирует резервное копирование.";
}
