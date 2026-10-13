using ICSharpCode.SharpZipLib.Zip;

namespace OneCBackup.Core;

public sealed class WebDavBackupExecutor(BackupCatalog catalog, FileBackupSnapshotStore snapshots,
    IArchivePasswordStore? passwords = null, Func<BackupJob, WebDavClient>? clientFactory = null) : IBackupExecutor
{
    public async Task<string> CreateAsync(BackupJob job, string workDirectory, CancellationToken cancellationToken)
    {
        if (job.Kind != DatabaseKind.WebDav) throw new ArgumentException("Выберите источник WebDAV.");
        var errors = job.Validate(); if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(job.WebDav!.TimeoutMinutes)); var token = timeout.Token;
        try { return await CreateCoreAsync(job, workDirectory, token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("Превышено время копирования WebDAV. Увеличьте ограничение времени или проверьте сеть."); }
    }
    private async Task<string> CreateCoreAsync(BackupJob job, string workDirectory, CancellationToken token)
    {
        BackupProgressContext.Report(BackupStage.ConnectingSource);
        using var client = clientFactory?.Invoke(job) ?? await WebDavClient.OpenAsync(job, token);
        var password = await ArchivePasswords.ResolveAsync(job.ArchivePasswordKey, passwords, token);
        var previous = await FileBackupBases.ReadAsync(job, client.SourceIdentity, catalog, snapshots, token);
        DiagnosticLog.Current.Write(DiagnosticEvent.SourceEnumerationStarted);
        BackupProgressContext.Report(BackupStage.ScanningSource);
        var scan = await client.ScanAsync(token);
        DiagnosticLog.Current.Write(DiagnosticEvent.SourceEnumerated, values: new(Count: scan.Count));
        var downloaded = 0; long downloadedBytes = 0;
        var oldFiles = previous?.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, FileBackupEntry>(StringComparer.OrdinalIgnoreCase);
        var id = Guid.NewGuid(); Directory.CreateDirectory(workDirectory);
        var destination = Path.Combine(Path.GetFullPath(workDirectory), BackupFileName.Create(job, $".files-{(previous is null ? "full" : "incremental")}.zip", uniqueId: id));
        var temporary = destination + ".partial"; var download = Path.Combine(workDirectory, id.ToString("N") + ".webdav-partial");
        var files = new List<FileBackupEntry>(); var changed = new List<string>();
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var zip = new ZipOutputStream(output) { IsStreamOwner = false, Password = password, UseZip64 = UseZip64.Dynamic })
                {
                    zip.SetLevel(6);
                    foreach (var item in scan.Where(entry => !entry.IsDirectory))
                    {
                        token.ThrowIfCancellationRequested(); oldFiles.TryGetValue(item.Path, out var old);
                        var modified = item.ModifiedUtc ?? old?.LastWriteUtc ?? DateTimeOffset.UtcNow;
                        // Only a strong server validator can avoid downloading. Dates/length alone do not establish equality.
                        if (old is not null && old.Path == item.Path && old.Length == item.Length
                            && WebDavClient.IsStrongETag(item.ETag) && old.WebDavETag == item.ETag)
                        { files.Add(old with { LastWriteUtc = modified }); continue; }
                        try
                        {
                            var hash = await client.DownloadAsync(item, download, token);
                            downloaded++; downloadedBytes += item.Length;
                            files.Add(new(item.Path, item.Length, hash, modified) { WebDavETag = item.ETag });
                            if (old is null || old.Path != item.Path || old.Length != item.Length || old.Sha256 != hash)
                            {
                                changed.Add(item.Path);
                                zip.PutNextEntry(Entry("data/" + item.Path, item.Length, password));
                                await using var input = new FileStream(download, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                                await BackupProgressContext.CopyAsync(input, zip, BackupStage.ArchivingFile, item.Length, token); zip.CloseEntry();
                            }
                        }
                        finally { File.Delete(download); }
                    }
                    var current = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var manifest = new FileBackupManifest { SnapshotId = id, FullSnapshotId = previous?.FullSnapshotId ?? id,
                        ParentSnapshotId = previous?.SnapshotId, SourceIdentity = client.SourceIdentity, SourceIsFile = false,
                        Depth = previous is null ? 0 : previous.Depth + 1, Files = files,
                        Directories = scan.Where(entry => entry.IsDirectory).Select(entry => entry.Path).ToArray(), ChangedFiles = changed,
                        DeletedFiles = oldFiles.Keys.Where(path => !current.Contains(path)).Order(StringComparer.Ordinal).ToArray() };
                    manifest.Validate();
                    await FileBackupZip.WriteManifestAsync(zip, manifest, password, token); zip.Finish();
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            // A denied/partial listing must never turn missing entries into deletions in a valid backup.
            DiagnosticLog.Current.Write(DiagnosticEvent.WebDavTransfersCompleted, values: new(Count: downloaded, Bytes: downloadedBytes));
            BackupProgressContext.Report(BackupStage.Verifying);
            var after = await client.ScanAsync(token);
            if (!scan.SequenceEqual(after)) throw new IOException("Состав или версии файлов WebDAV изменились во время копирования. Повторите запуск.");
            DiagnosticLog.Current.Write(DiagnosticEvent.SourceRevalidated, values: new(Count: after.Count));
            await FileBackupZip.VerifyAsync(temporary, password, token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, destination); return destination;
        }
        finally { File.Delete(temporary); File.Delete(download); }
    }
    private static ZipEntry Entry(string name, long length, string? password) => new(name)
    { Size = length, AESKeySize = password is null ? 0 : 256, DateTime = DateTime.UtcNow };
}
