namespace SPBackup.Core;

/// <summary>Persists an exact receipt before catalog registration so recovery can repeat registration safely.</summary>
internal static class BackupDeliveryJournal
{
    private sealed record Entry(Guid RunId, Guid JobId, int DestinationIndex, string StorageIdentity, BackupRecord? Record, string? ArchiveName);

    private static string DirectoryFor(string workDirectory, Guid runId) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(workDirectory))!, ".delivery-" + runId.ToString("N"));

    private static AgentJsonFile<Entry> FileFor(string directory, int index) =>
        new(Path.Combine(directory, index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json"));

    internal static void Prepare(string workDirectory, Guid runId) =>
        PrivateBackupDirectory.Create(DirectoryFor(workDirectory, runId));

    internal static Task WriteAsync(string stagingDirectory, Guid? runId, BackupJob target, int index, BackupRecord? receipt, string? archiveName = null)
    {
        if (runId is not { } id) return Task.CompletedTask;
        var directory = DirectoryFor(Path.GetDirectoryName(Path.GetFullPath(stagingDirectory))!, id);
        FileBackupPaths.RejectReparse(directory);
        return FileFor(directory, index).WriteAsync(new(id, target.Id, index, StorageAddress.Identity(target), receipt, archiveName), CancellationToken.None);
    }

    internal static void Remove(string workDirectory, Guid runId)
    {
        var path = DirectoryFor(workDirectory, runId);
        if (Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(path, recursive: true);
    }

    internal static async Task<IReadOnlyList<DestinationResult>> ReconcileAsync(BackupRun run, BackupJob job, string workRoot,
        BackupCatalog catalog, IBackupStorage storage, CancellationToken token)
    {
        var expectedWorkDirectory = Path.GetFullPath(Path.Combine(job.TemporaryDirectory ?? workRoot, run.RunId.ToString("N")));
        if (!string.Equals(expectedWorkDirectory, run.WorkDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return [];
        var directory = DirectoryFor(run.WorkDirectory, run.RunId);
        if (!Directory.Exists(directory)) return [];

        var results = new List<DestinationResult>();
        var targets = job.StorageJobs;
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var description = new BackupDestination { StorageKind = target.StorageKind,
                DestinationDirectory = target.DestinationDirectory, Ftp = target.Ftp, GoogleDrive = target.GoogleDrive, KeepLast = target.KeepLast }.Description;
            BackupRecord? record = null;
            try
            {
                var entry = await FileFor(directory, index).ReadBoundedAsync(64 * 1024, token);
                if (entry is not null && (entry.RunId != run.RunId || entry.JobId != job.Id
                    || entry.DestinationIndex != index || entry.StorageIdentity != StorageAddress.Identity(target)))
                    throw new InvalidDataException("Квитанция доставки не соответствует запуску и хранилищу.");
                if (entry?.Record is null)
                {
                    var candidate = entry?.ArchiveName is { Length: > 0 } name ? $" Ожидался архив {name}." : "";
                    results.Add(new(description, null, "Запись архива не подтверждена." + candidate + " Проверьте файлы в назначении."));
                    continue;
                }
                record = entry.Record;
                if (record.StorageIdentity != entry.StorageIdentity || record.JobId != job.Id || record.Id == Guid.Empty)
                    throw new InvalidDataException("Копия в квитанции не соответствует заданию и хранилищу.");

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var verifier = new BackupCopyVerifier(storage);
                var records = await catalog.ReadAsync(timeout.Token);
                var chain = record.Backup.FileSnapshot is null ? [record] : FileBackupChains.Resolve(record, records);
                foreach (var archive in chain)
                    await verifier.VerifyAsync(archive, target, timeout.Token);
                await catalog.RegisterAsync(target, record, CancellationToken.None);
                results.Add(new(description, record, null));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException
                or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                var location = record is null ? "" : $" Архив по квитанции: {record.Backup.Location}.";
                var detail = error is OperationCanceledException ? "Истекло время проверки." : error.Message;
                results.Add(new(description, null, "Доставка после прерывания не подтверждена." + location + " " + detail));
            }
        }

        return results;
    }
}
