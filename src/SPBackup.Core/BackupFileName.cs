using System.Globalization;
using System.Text;

namespace SPBackup.Core;

public static class BackupFileName
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Counts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Recent = new();
    public static string Create(BackupJob job, string suffix, DateTimeOffset? createdAt = null)
    {
        var name = new StringBuilder();
        foreach (var character in job.Name ?? "")
            name.Append(char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character);
        var safe = name.ToString().Trim().TrimEnd('.', ' ');
        if (safe.Length > 80) safe = safe[..80].TrimEnd('.', ' ');
        if (safe.Length != 0 && char.IsHighSurrogate(safe[^1])) safe = safe[..^1];
        if (job.Kind == DatabaseKind.OneCFile && safe.EndsWith(".1CD", StringComparison.OrdinalIgnoreCase)) safe = safe[..^4].TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "Backup";
        var stamp = (createdAt ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        // The date suffix also makes reserved Windows device names (CON, AUX, ...) ordinary filenames.
        var stem = $"{safe}_{stamp}";
        // Human-readable same-second collisions. Delivery still refuses to overwrite another file.
        lock (Gate)
        {
            var key = stem + suffix;
            if (Counts.TryGetValue(key, out var count))
            {
                Counts[key] = checked(count + 1);
                return $"{stem} ({count + 1}){suffix}";
            }
            Counts.Add(key, 1); Recent.Enqueue(key);
            if (Recent.Count > 4096) Counts.Remove(Recent.Dequeue());
            return stem + suffix;
        }
    }
}
