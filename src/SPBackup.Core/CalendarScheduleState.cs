using System.Security.Cryptography;
using System.Text;

namespace SPBackup.Core;

public sealed record MissedScheduledRun(DateTimeOffset ScheduledAt, bool CatchUpAttempted);

// The owning dispatcher holds the same profile lease used for startup schedules.
internal sealed class CalendarScheduleState(string path)
{
    internal sealed record Cursor(string Schedule, DateTimeOffset Next, MissedScheduledRun? LastMissed = null);
    private readonly AgentJsonFile<Dictionary<Guid, Cursor>> file = new(path);

    internal static string PathBesideStartupMarker(string startupMarker) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(startupMarker))!, "calendar-schedule.json");

    internal static bool Tracks(BackupJob job) =>
        job.Enabled && job.ScheduleKind is ScheduleKind.Daily or ScheduleKind.Weekly;

    internal static string Identity(BackupJob job, TimeZoneInfo zone)
    {
        var zoneIdentity = job.TimeZoneId == BackupSchedule.LocalTimeZone ? zone.ToSerializedString() : job.TimeZoneId;
        var settings = FormattableString.Invariant($"{job.ScheduleKind}|{(int)job.DaysOfWeek}|{zoneIdentity}|")
            + string.Join(',', job.ScheduledTimes.Select(time => time.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings)));
    }

    internal async Task<Dictionary<Guid, Cursor>> ReadAsync(CancellationToken token)
    {
        var cursors = await file.ReadAsync(token) ?? [];
        if (cursors.Any(entry => entry.Key == Guid.Empty || entry.Value is not { Schedule.Length: 64, Next.Ticks: > 0 }
            || entry.Value.LastMissed is { } missed && (missed.ScheduledAt == default || missed.ScheduledAt >= entry.Value.Next)))
            throw new InvalidDataException("Повреждено состояние календарного расписания.");
        return cursors;
    }

    internal Task WriteAsync(Dictionary<Guid, Cursor> cursors, CancellationToken token) => file.WriteAsync(cursors, token);
}
