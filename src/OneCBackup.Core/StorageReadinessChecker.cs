using System.Security.Cryptography;

namespace OneCBackup.Core;

public sealed record StorageCheckResult(string Destination, bool Succeeded, string? Error);
public sealed record StorageCheckReport(string TemporaryDirectory, string? TemporaryError,
    IReadOnlyList<StorageCheckResult> Destinations, bool Cancelled);

/// <summary>Checks write, read and deletion using only uniquely named disposable files, never a backup catalog.</summary>
public sealed class StorageReadinessChecker(IBackupStorage storage, string defaultWorkRoot)
{
    public async Task<StorageCheckReport> CheckAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        var errors = job.Validate();
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(job.TemporaryDirectory ?? defaultWorkRoot);
        var work = Path.Combine(root, "storage-check-" + Guid.NewGuid().ToString("N"));
        var probe = Path.Combine(work, "onec-storage-check-" + Guid.NewGuid().ToString("N") + ".probe");
        var results = new List<StorageCheckResult>();
        string? temporaryError = null;
        var cancelled = false;
        var ownsWork = false;
        try
        {
            if (File.Exists(root)) throw new IOException("Вместо временной папки указан файл. Выберите другую папку.");
            if (Directory.Exists(work)) throw new IOException("Тестовая рабочая папка уже существует.");
            Directory.CreateDirectory(work);
            ownsWork = true;
            var bytes = RandomNumberGenerator.GetBytes(4096);
            await using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            if (!(await File.ReadAllBytesAsync(probe, cancellationToken)).AsSpan().SequenceEqual(bytes))
                throw new IOException("Не пройдена проверка чтения временного файла.");

            foreach (var target in job.StorageJobs)
            {
                var label = new BackupDestination { StorageKind = target.StorageKind,
                    DestinationDirectory = target.DestinationDirectory, Ftp = target.Ftp, KeepLast = target.KeepLast }.Description;
                if (cancellationToken.IsCancellationRequested)
                { cancelled = true; results.Add(new(label, false, "Проверка отменена.")); continue; }
                StoredBackup? stored = null;
                string? error = null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await storage.PrepareAsync(target, timeout.Token);
                    stored = await storage.StoreAsync(probe, target, timeout.Token);
                    await storage.VerifyAsync(stored, target, timeout.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { cancelled = true; error = "Проверка отменена."; }
                catch (OperationCanceledException) { error = "Превышено время проверки хранилища (30 секунд)."; }
                catch (Exception ex)
                {
                    error = ex is IOException && target.StorageKind != StorageKind.Ftp && File.Exists(target.DestinationDirectory)
                        ? "Вместо папки хранилища указан файл. Выберите другую папку." : ex.Message;
                }
                finally
                {
                    if (stored is not null)
                    {
                        // Cancellation cannot abandon a delivered probe; cleanup has its own bounded token.
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await storage.DeleteAsync(stored, target, cleanup.Token); }
                        catch (Exception ex)
                        {
                            error = (error is null ? "" : error + " ")
                                + "Не удалось удалить тестовый файл: " + stored.Location + ". " + ex.Message;
                        }
                    }
                }
                results.Add(new(label, error is null, error));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { cancelled = true; temporaryError = "Проверка временной папки отменена."; }
        catch (Exception ex) { temporaryError = ex.Message; }
        finally
        {
            if (ownsWork)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { temporaryError = (temporaryError is null ? "" : temporaryError + " ") + "Не удалось удалить тестовую рабочую папку: " + work + ". " + ex.Message; }
            }
        }
        return new(root, temporaryError, results, cancelled || cancellationToken.IsCancellationRequested);
    }
}
