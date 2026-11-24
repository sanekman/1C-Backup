using SPBackup.Server.Monitoring;

namespace SPBackup.Server;

internal sealed record MonitoringProgressDisplay(string Label, DateTimeOffset ObservedUtc, bool Stale)
{
    internal static MonitoringProgressDisplay? Create(MonitoredProgress? progress, DateTimeOffset? observedUtc,
        DateTimeOffset receivedUtc, bool revoked, bool completed, DateTimeOffset cutoff)
    {
        if (revoked || completed || progress is null) return null;
        var observed = observedUtc ?? receivedUtc;
        var stage = progress.Stage switch
        {
            "ConnectingSource" => "Подключение к источнику",
            "PreparingStorage" or "ConnectingStorage" => "Подключение к хранилищу",
            "CreatingBackup" or "ScanningSource" => "Создание копии",
            "ProcessingFiles" => "Обработка файлов",
            "DownloadingFile" => "Скачивание файла",
            "ArchivingFile" or "Archiving" or "ArchivingFiles" => "Архивация",
            "DownloadingAndArchivingFiles" => "Скачивание и архивация",
            "CopyingToStorage" => "Доставка",
            "Verifying" => "Проверка",
            "Retention" => "Очистка старых копий",
            "Waiting" => "Ожидание",
            _ => "Выполняется"
        };
        var label = progress.Percent is { } percent ? $"{stage} · {percent}%" : stage;
        return new(label, observed, observed < cutoff || receivedUtc < cutoff);
    }
}
