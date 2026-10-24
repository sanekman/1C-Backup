using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace OneCBackup.Core;

public sealed record DiagnosticBundleResult(string Path, int LogFiles, int Events, int Skipped, int Runs, long Bytes);

/// <summary>Exports only typed diagnostic events and an allowlisted run summary, never raw profile files.</summary>
public sealed class DiagnosticBundle(string profileDirectory, string version, string revision)
{
    public async Task<DiagnosticBundleResult> ExportAsync(string destination, CancellationToken token = default)
    {
        var profile = Path.GetFullPath(profileDirectory);
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("Укажите полный путь архива диагностики.");
        var target = Path.GetFullPath(destination);
        FileBackupPaths.RequireOutside(profile, target, "Сохраните диагностику вне рабочего профиля программы.");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Файл уже существует. Выберите другое имя.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("Папка для диагностики не существует.");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        var own = false; var files = 0; var events = 0; var skipped = 0; var runs = 0;
        DiagnosticLog.Current.Write(DiagnosticEvent.DiagnosticExportStarted);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                own = true;
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
                {
                    var directory = Path.Combine(profile, "logs"); long consumed = 0;
                    if (Directory.Exists(directory))
                    {
                        DiagnosticLog.RequireRealDirectory(directory);
                        var candidates = Directory.EnumerateFiles(directory).Where(path => DiagnosticLog.IsOwnFile(Path.GetFileName(path)))
                            .Select(path => new FileInfo(path)).Where(file => file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-14))
                            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
                        foreach (var file in candidates)
                        {
                            token.ThrowIfCancellationRequested();
                            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > 32 * 1024 * 1024
                                || consumed + file.Length > 50 * 1024 * 1024) { skipped++; continue; }
                            byte[] bytes;
                            try
                            {
                                await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous);
                                var length = input.Length;
                                if (length > 32 * 1024 * 1024 || consumed + length > 50 * 1024 * 1024) { skipped++; continue; }
                                bytes = new byte[(int)length]; await input.ReadExactlyAsync(bytes, token); consumed += length;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; continue; }
                            // Serialize only recognized structured records. Malformed / incomplete / unexpected fields are excluded.
                            using var buffer = new MemoryStream();
                            string text;
                            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
                            if (lastNewline != bytes.Length - 1) skipped++;
                            if (lastNewline < 0) continue;
                            try { text = new UTF8Encoding(false, true).GetString(bytes, 0, lastNewline + 1); }
                            catch (DecoderFallbackException) { skipped++; continue; }
                            var lines = text.Split('\n');
                            if (lines[^1].Length != 0) skipped++;
                            foreach (var line in lines.Take(lines.Length - 1))
                            {
                                token.ThrowIfCancellationRequested();
                                try
                                {
                                    if (Encoding.UTF8.GetByteCount(line) > 32768) { skipped++; continue; }
                                    var entry = JsonSerializer.Deserialize<DiagnosticEntry>(line, DiagnosticLog.Json);
                                    if (entry is null || entry.FormatVersion != 1 || !Enum.IsDefined(entry.Event) || !Enum.IsDefined(entry.Level)
                                        || !Enum.IsDefined(entry.Component) || entry.SessionId == Guid.Empty || entry.Utc.Offset != TimeSpan.Zero)
                                    { skipped++; continue; }
                                    var clean = JsonSerializer.SerializeToUtf8Bytes(entry, DiagnosticLog.Json);
                                    await buffer.WriteAsync(clean, token); buffer.WriteByte((byte)'\n'); events++;
                                }
                                catch (JsonException) { skipped++; }
                            }
                            if (buffer.Length != 0)
                            {
                                var entry = zip.CreateEntry("logs/" + file.Name, CompressionLevel.Optimal);
                                await using var stream = entry.Open(); buffer.Position = 0; await buffer.CopyToAsync(stream, token); files++;
                            }
                        }
                    }
                    object runSummary; string historyStatus;
                    try
                    {
                        var historyPath = Path.Combine(profile, "runs.json");
                        if (File.Exists(historyPath) && (new FileInfo(historyPath).Length > 32 * 1024 * 1024
                            || File.GetAttributes(historyPath).HasFlag(FileAttributes.ReparsePoint))) throw new InvalidDataException("History unavailable");
                        var history = await new BackupRunHistory(historyPath).ReadAsync(token);
                        var selected = history.OrderByDescending(run => run.StartedUtc).Take(1000).ToArray(); runs = selected.Length;
                        runSummary = selected.Select(run => new { run.RunId, run.JobId, run.StartedUtc, run.FinishedUtc, run.Status,
                            DurationMilliseconds = run.FinishedUtc is { } end ? (long?)(end - run.StartedUtc).TotalMilliseconds : null,
                            BackupBytes = run.Backup?.Length, Destinations = run.Destinations?.Count,
                            FailedDestinations = run.Destinations?.Count(item => item.Error is not null), Warnings = run.Warnings?.Count ?? 0 });
                        historyStatus = "Available";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    { runSummary = Array.Empty<object>(); historyStatus = "Unavailable"; skipped++; }
                    await WriteJson(zip, "runs-summary.json", runSummary, token);
                    await WriteJson(zip, "support-info.json", new { Application = "1C-Backup", FormatVersion = 1, Version = version, Revision = revision,
                        CreatedUtc = DateTimeOffset.UtcNow, DotNet = Environment.Version.ToString(), Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        System = RuntimeInformation.OSDescription, TimeZone = TimeZoneInfo.Local.Id, LogFiles = files, Events = events, Skipped = skipped,
                        Runs = runs, HistoryStatus = historyStatus, LoggingFailure = DiagnosticLog.Current.LastWriteFailure }, token);
                    var readme = zip.CreateEntry("READ-ME.txt");
                    await using var readmeStream = readme.Open();
                    await readmeStream.WriteAsync(Encoding.UTF8.GetBytes("1C-Backup — диагностика\nОтправьте этот ZIP для анализа. Укажите примерное время проблемы и ожидаемый результат.\nlogs/*.jsonl: UTC, компонент, версия, ID задания/запуска, этап, коды и стек ошибки.\nruns-summary.json: до 1000 последних запусков без названий, путей и текста ошибок.\nНе включены настройки, пароли, токены, адреса серверов, исходные файлы, копии и сырой вывод SQL/1С.\nТекст Exception.Message намеренно отсутствует: сторонние программы могут включать в него секреты.\nПропущенные неполные/недоступные записи учитываются в support-info.json.\n"), token);
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested(); File.Move(temporary, target);
            var resultBytes = new FileInfo(target).Length;
            DiagnosticLog.Current.Write(DiagnosticEvent.DiagnosticExportCompleted, values: new(Count: events, Bytes: resultBytes));
            return new(target, files, events, skipped, runs, resultBytes);
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DiagnosticExportFailed, DiagnosticLevel.Error, ex); throw; }
        finally { if (own) File.Delete(temporary); }
    }
    private static async Task WriteJson(ZipArchive zip, string name, object value, CancellationToken token)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal); await using var output = entry.Open();
        await JsonSerializer.SerializeAsync(output, value, new JsonSerializerOptions(DiagnosticLog.Json) { WriteIndented = true }, token);
    }
}
