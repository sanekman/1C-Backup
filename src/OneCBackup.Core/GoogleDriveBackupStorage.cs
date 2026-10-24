using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OneCBackup.Core;

/// <summary>Resumable chunked upload and SHA-256 readback; never scans/deletes foreign files.</summary>
public sealed class GoogleDriveBackupStorage(GoogleDriveClient client) : IBackupStorage
{
    private static void Validate(BackupJob job)
    {
        if (job.StorageKind != StorageKind.GoogleDrive || job.Validate().Count != 0)
            throw new InvalidDataException("Некорректное хранилище Google Drive.");
    }
    public Task PrepareAsync(BackupJob job, CancellationToken cancellationToken = default)
    { Validate(job); return client.CheckFolderAsync(job.DestinationDirectory, cancellationToken); }

    private async Task CheckFileAsync(string id, BackupJob job, CancellationToken token)
    {
        using var json = await client.JsonAsync(HttpMethod.Get, "drive/v3/files/" + id + "?fields=id,trashed,parents,appProperties&supportsAllDrives=true", null, token);
        var r = json.RootElement;
        if (r.GetProperty("trashed").GetBoolean() || !r.TryGetProperty("parents", out var parents)
            || !parents.EnumerateArray().Any(p => p.GetString() == job.DestinationDirectory)
            || !r.TryGetProperty("appProperties", out var properties)
            || !properties.TryGetProperty("onecJob", out var owner) || owner.GetString() != job.Id.ToString("N"))
            throw new InvalidDataException("Файл Google Drive не принадлежит папке и заданию. Операция запрещена.");
    }
    public async Task<StoredBackup> StoreAsync(string completedBackupPath, BackupJob job, CancellationToken cancellationToken = default)
    {
        Validate(job); BackupProgressContext.Report(BackupStage.ConnectingStorage); await client.CheckFolderAsync(job.DestinationDirectory, cancellationToken);
        await using var input = new FileStream(completedBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (input.Length == 0) throw new InvalidDataException("Пустую резервную копию отправлять нельзя.");
        var length = input.Length;
        BackupProgressContext.Report(BackupStage.Verifying);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)); input.Position = 0;
        using var metadata = await client.JsonAsync(HttpMethod.Post, "drive/v3/files?fields=id&supportsAllDrives=true", new
        { name = Path.GetFileName(completedBackupPath), mimeType = "application/octet-stream", parents = new[] { job.DestinationDirectory },
            appProperties = new { onecJob = job.Id.ToString("N"), onecState = "pending" } }, cancellationToken);
        var id = metadata.RootElement.GetProperty("id").GetString();
        if (!GoogleDriveSettings.IsId(id)) throw new InvalidDataException("Google Drive не создал файл копии.");
        var stored = new StoredBackup("gdrive:" + job.DestinationDirectory + "/" + id, length, sha)
        { OriginalFileName = Path.GetFileName(completedBackupPath) };
        try
        {
            using var initial = await client.SendAsync(HttpMethod.Patch, "upload/drive/v3/files/" + id + "?uploadType=resumable&supportsAllDrives=true",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), cancellationToken, length);
            GoogleDriveClient.EnsureSuccess(initial);
            var upload = initial.Headers.Location;
            if (upload is null || !upload.IsAbsoluteUri || upload.Host != "www.googleapis.com" || upload.Scheme != "https" || upload.Port != 443
                || upload.AbsolutePath != "/upload/drive/v3/files/" + id
                || upload.UserInfo.Length != 0 || upload.Fragment.Length != 0)
                throw new InvalidDataException("Некорректный адрес загрузки Google Drive.");
            var buffer = new byte[8 * 1024 * 1024]; long completed = 0;
            BackupProgressContext.Report(BackupStage.CopyingToStorage, 0, length);
            while (completed < length)
            {
                var size = (int)Math.Min(buffer.Length, length - completed);
                await input.ReadExactlyAsync(buffer.AsMemory(0, size), cancellationToken);
                using var content = new ByteArrayContent(buffer, 0, size);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentRange = new ContentRangeHeaderValue(completed, completed + size - 1, length);
                using var response = await client.SendAsync(HttpMethod.Put, upload.AbsoluteUri, content, cancellationToken);
                completed += size;
                if (completed < length)
                {
                    if ((int)response.StatusCode != 308 || !response.Headers.TryGetValues("Range", out var ranges)
                        || ranges.SingleOrDefault() != "bytes=0-" + (completed - 1))
                        throw new IOException("Google Drive не подтвердил загруженную часть копии.");
                }
                else GoogleDriveClient.EnsureSuccess(response);
                BackupProgressContext.Report(BackupStage.CopyingToStorage, completed, length);
            }
            await VerifyAsync(stored, job, cancellationToken);
            using var complete = await client.JsonAsync(HttpMethod.Patch, "drive/v3/files/" + id + "?fields=id&supportsAllDrives=true",
                new { appProperties = new { onecJob = job.Id.ToString("N"), onecState = "complete" } }, cancellationToken);
            return stored;
        }
        catch
        {
            // Only our exact pending file; bounded independent cancellation allows cleanup.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await CheckFileAsync(id!, job, cleanup.Token);
                using var response = await client.SendAsync(HttpMethod.Delete, "drive/v3/files/" + id + "?supportsAllDrives=true", null, cleanup.Token);
                GoogleDriveClient.EnsureSuccess(response);
            }
            catch (Exception) { /* Never register the pending file or obscure the original failure. */ }
            throw;
        }
    }
    public async Task CopyToAsync(StoredBackup backup, BackupJob job, Stream destination, CancellationToken cancellationToken = default)
    {
        Validate(job); var id = GoogleDriveSettings.FileId(job, backup);
        await CheckFileAsync(id, job, cancellationToken);
        using var response = await client.SendAsync(HttpMethod.Get, "drive/v3/files/" + id + "?alt=media&supportsAllDrives=true", null, cancellationToken);
        GoogleDriveClient.EnsureSuccess(response);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[81920]; long copied = 0; int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            copied += count;
            if (copied > backup.Length) throw new InvalidDataException("Размер копии Google Drive изменён.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            BackupProgressContext.Report(BackupStage.Verifying, copied, backup.Length);
        }
        if (copied != backup.Length) throw new InvalidDataException("Размер копии Google Drive изменён.");
    }
    public async Task VerifyAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default)
    {
        using var output = new HashSink();
        await CopyToAsync(backup, job, output, cancellationToken);
        if (!string.Equals(Convert.ToHexString(output.Hash.GetHashAndReset()), backup.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256 копии Google Drive не совпадает с журналом.");
    }
    public async Task DeleteAsync(StoredBackup backup, BackupJob job, CancellationToken cancellationToken = default)
    {
        await VerifyAsync(backup, job, cancellationToken);
        var id = GoogleDriveSettings.FileId(job, backup);
        await CheckFileAsync(id, job, cancellationToken);
        using var response = await client.SendAsync(HttpMethod.Delete, "drive/v3/files/" + id + "?supportsAllDrives=true", null, cancellationToken);
        GoogleDriveClient.EnsureSuccess(response);
    }
    private sealed class HashSink : Stream
    {
        public readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Hash.AppendData(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Hash.AppendData(buffer.Span); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) Hash.Dispose(); base.Dispose(disposing); }
    }
}
