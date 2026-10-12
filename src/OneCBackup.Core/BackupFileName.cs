using System.Globalization;
using System.Text;

namespace OneCBackup.Core;

public static class BackupFileName
{
    public static string Create(BackupJob job, string suffix, DateTimeOffset? createdAt = null, Guid? uniqueId = null)
    {
        var name = new StringBuilder();
        foreach (var character in job.Name ?? "")
            name.Append(char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character);
        var safe = name.ToString().Trim().TrimEnd('.', ' ');
        if (safe.Length > 80) safe = safe[..80].TrimEnd('.', ' ');
        if (safe.Length != 0 && char.IsHighSurrogate(safe[^1])) safe = safe[..^1];
        if (safe.Length == 0) safe = "Backup";
        var stamp = (createdAt ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        // The date suffix also makes reserved Windows device names (CON, AUX, ...) ordinary filenames.
        return $"{safe}_{stamp}_{(uniqueId ?? Guid.NewGuid()).ToString("N")[..12]}{suffix}";
    }
}
