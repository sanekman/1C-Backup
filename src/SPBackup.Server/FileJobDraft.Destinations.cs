using System.Globalization;

namespace SPBackup.Server;

public sealed class FileDestinationDraft
{
    public string Destination { get; set; } = "";
    public string Keep { get; set; } = "";
}

public sealed partial class FileJobDraft
{
    public const int MaxAdditionalDestinations = 7;
    private List<FileDestinationDraft> additionalDestinations = [];
    public List<FileDestinationDraft> AdditionalDestinations
    {
        get => additionalDestinations;
        set => additionalDestinations = value ?? [];
    }

    private IReadOnlyList<string> ValidateAdditionalDestinations(string source)
    {
        var errors = new List<string>();
        if (AdditionalDestinations.Count > MaxAdditionalDestinations) errors.Add("Можно указать не более 8 хранилищ: основное и 7 дополнительных.");
        var paths = new List<string>();
        if (FileConfigurationContract.WindowsPath(Destination?.Trim())) paths.Add(Destination!.Trim());
        for (var index = 0; index < AdditionalDestinations.Count; index++)
        {
            var row = AdditionalDestinations[index]; var label = $"Хранилище {index + 2}: ";
            if (row is null) { errors.Add(label + "укажите параметры папки."); continue; }
            if (!string.IsNullOrWhiteSpace(row.Keep) && (!int.TryParse(row.Keep, out var count) || count is < 1 or > 10000))
                errors.Add(label + "укажите от 1 до 10000 архивов или оставьте лимит пустым, чтобы использовать основной.");
            var path = row.Destination?.Trim();
            if (!FileConfigurationContract.WindowsPath(path)) { errors.Add(label + "укажите полный путь на диске агента или сетевой путь UNC."); continue; }
            if (FileConfigurationContract.WindowsPath(source) && FileConfigurationContract.Overlap(source, path!))
                errors.Add(label + "папка копий не должна пересекаться с источником.");
            if (!string.IsNullOrWhiteSpace(Temporary) && FileConfigurationContract.Overlap(Temporary.Trim(), path!))
                errors.Add(label + "папка копий не должна пересекаться с рабочей папкой.");
            if (paths.Any(previous => FileConfigurationContract.Overlap(previous, path!)))
                errors.Add(label + "уже указана эта папка либо одна папка копий содержит другую. Выберите отдельную папку.");
            paths.Add(path!);
        }
        return errors;
    }

    private IReadOnlyList<ManagedFileDestination>? BuildAdditionalDestinations() => AdditionalDestinations.Count == 0 ? null
        : AdditionalDestinations.Select(row => new ManagedFileDestination {
            StorageKind = row.Destination.Trim().StartsWith(@"\\") ? "NetworkShare" : "LocalDirectory",
            DestinationDirectory = row.Destination.Trim(), KeepLast = string.IsNullOrWhiteSpace(row.Keep) ? null : int.Parse(row.Keep, CultureInfo.InvariantCulture)
        }).ToArray();
}
