using System.Text.Json.Serialization;

namespace OneCBackup.Core;

/// <summary>A second delivery destination; the database and archive settings belong to the job.</summary>
public sealed record BackupDestination
{
    public StorageKind StorageKind { get; init; }
    public string DestinationDirectory { get; init; } = "";
    public FtpSettings? Ftp { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeepLast { get; init; }

    [JsonIgnore]
    public string Description => (StorageKind switch
    {
        StorageKind.LocalDirectory => "Локальный диск",
        StorageKind.NetworkShare => "Сетевая папка",
        StorageKind.Ftp => (Ftp?.UseTls == true ? "FTPS" : "FTP") + " · " + Ftp?.Host + ":" + Ftp?.Port,
        _ => "Неизвестное хранилище"
    }) + " · " + DestinationDirectory + (KeepLast is { } count ? " · копий: " + count : " · как в основном хранилище");

    public BackupJob ApplyTo(BackupJob job) => job with
    {
        StorageKind = StorageKind, DestinationDirectory = DestinationDirectory, Ftp = Ftp,
        KeepLast = KeepLast ?? job.KeepLast,
        AdditionalDestinations = null
    };
}
