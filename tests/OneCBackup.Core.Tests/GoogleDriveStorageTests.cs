using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class GoogleDriveStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "drive-test-" + Guid.NewGuid());
    private static GoogleDriveCredentials Credentials => new("fixture.apps.googleusercontent.com", "fixture-secret", "fixture-refresh");
    private static BackupJob Job => new() { Name = "Drive", Source = "fixture", StorageKind = StorageKind.GoogleDrive,
        DestinationDirectory = "Folder_A", GoogleDrive = new() { CredentialKey = "connection" } };
    public GoogleDriveStorageTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Drive : HttpMessageHandler
    {
        public string Folder = "Folder_A", Owner = "", Id = "File_1";
        public byte[] Bytes = [];
        public bool Deleted, Complete, Corrupt, FailUpload, Foreign;
        public int Chunks, TokenRequests;
        public string SessionHost = "www.googleapis.com";
        public string? SessionFile;
        public CancellationTokenSource? CancelUpload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Assert.Equal("https", uri.Scheme);
            Assert.DoesNotContain("fixture-secret", uri.AbsoluteUri);
            Assert.DoesNotContain("fixture-refresh", uri.AbsoluteUri);
            if (uri.Host == "oauth2.googleapis.com")
            { TokenRequests++; return Json(new { access_token = "fixture-access", expires_in = 3600 }); }
            Assert.Equal("www.googleapis.com", uri.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-access", request.Headers.Authorization.Parameter);
            var path = uri.AbsolutePath;
            if (path.StartsWith("/upload/") && request.Method == HttpMethod.Patch)
            {
                Assert.True(request.Headers.Contains("X-Upload-Content-Length"));
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri("https://" + SessionHost + "/upload/drive/v3/files/" + (SessionFile ?? Id) + "?upload_id=fixture");
                return response;
            }
            if (path.StartsWith("/upload/") && request.Method == HttpMethod.Put)
            {
                Chunks++;
                if (CancelUpload is not null) { CancelUpload.Cancel(); token.ThrowIfCancellationRequested(); }
                if (FailUpload) return Json(new { }, HttpStatusCode.ServiceUnavailable);
                var content = await request.Content!.ReadAsByteArrayAsync(token);
                var range = request.Content.Headers.ContentRange!;
                Assert.Equal(Bytes.Length, range.From);
                Assert.Equal(Bytes.Length + content.Length - 1, range.To);
                Bytes = Bytes.Concat(content).ToArray();
                if (range.To + 1 == range.Length) return Json(new { id = Id });
                var response = new HttpResponseMessage((HttpStatusCode)308);
                response.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (Bytes.Length - 1)); return response;
            }
            if (request.Method == HttpMethod.Post)
            {
                using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var r = data.RootElement;
                Folder = r.GetProperty("parents")[0].GetString()!;
                Owner = r.GetProperty("appProperties").GetProperty("onecJob").GetString()!;
                return Json(new { id = Id });
            }
            if (request.Method == HttpMethod.Patch)
            { Complete = true; return Json(new { id = Id }); }
            if (request.Method == HttpMethod.Delete)
            { Deleted = true; return new(HttpStatusCode.NoContent); }
            if (path.EndsWith("/Folder_A"))
                return Json(new { id = Folder, mimeType = "application/vnd.google-apps.folder", trashed = false, capabilities = new { canAddChildren = true } });
            if (uri.Query.Contains("alt=media"))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? Bytes.Select(b => (byte)(b ^ 1)).ToArray() : Bytes) };
            return Json(new { id = Id, trashed = false, parents = new[] { Foreign ? "ForeignFolder" : Folder }, appProperties = new { onecJob = Owner } });
        }
    }
    [Fact]
    public async Task LargeCopyUploadsInPartsReadsBackAndExportsExactBytes()
    {
        using var drive = new Drive(); using var http = new HttpClient(drive);
        var storage = new GoogleDriveBackupStorage(new(http, Credentials)); var job = Job;
        var file = Path.Combine(directory, "Бухгалтерия.zip"); var bytes = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 301);
        await File.WriteAllBytesAsync(file, bytes);
        await storage.PrepareAsync(job);
        var stored = await storage.StoreAsync(file, job);
        Assert.Equal(2, drive.Chunks); Assert.True(drive.Complete); Assert.False(drive.Deleted); Assert.Equal(1, drive.TokenRequests);
        Assert.Equal("gdrive:Folder_A/File_1", stored.Location);
        Assert.Equal("Бухгалтерия.zip", stored.OriginalFileName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), stored.Sha256);
        using var exported = new MemoryStream(); await storage.CopyToAsync(stored, job, exported); Assert.Equal(bytes, exported.ToArray());
        await storage.VerifyAsync(stored, job);
        await storage.DeleteAsync(stored, job); Assert.True(drive.Deleted);
    }
    private sealed class CredentialStore : IFtpCredentialStore
    {
        public Task<NetworkCredential> GetAsync(string key, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new NetworkCredential(Credentials.ClientId, JsonSerializer.Serialize(Credentials))); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MixedDeliveryRegistersSuccessfulDestinationsIndependently(bool googleFails)
    {
        using var drive = new Drive { FailUpload = googleFails }; using var http = new HttpClient(drive);
        var router = new RoutedBackupStorage(new CredentialStore(), directory, googleCredentials: new CredentialStore(),
            googleFactory: value => new GoogleDriveBackupStorage(new(http, value)));
        var local = Path.Combine(directory, "local");
        var job = Job with { KeepLast = 2, AdditionalDestinations = [new() { StorageKind = StorageKind.LocalDirectory,
            DestinationDirectory = local, KeepLast = 9 }] };
        var file = Path.Combine(directory, "backup.zip"); await File.WriteAllBytesAsync(file, [1, 2, 3]);
        var catalog = new BackupCatalog(Path.Combine(directory, "catalog.json"));
        var completion = new BackupCompletion(router, catalog);
        await completion.PrepareAsync(job);
        var result = await completion.CompleteManyAsync(job, file, directory, DateTimeOffset.UtcNow);
        var records = await catalog.ReadAsync();
        Assert.Equal(googleFails ? 1 : 2, records.Count);
        Assert.True(File.Exists(Path.Combine(local, "backup.zip")));
        Assert.Contains(records, record => record.StorageIdentity.StartsWith("LocalDirectory:"));
        if (!googleFails) Assert.Contains(records, record => record.StorageIdentity == "GoogleDrive:Folder_A");
        Assert.Equal(9, job.StorageJobs[1].KeepLast);
    }
    [Theory]
    [InlineData(true, false)] [InlineData(false, true)]
    public async Task UploadOrVerificationFailureCleansOnlyCreatedFileAndNeverCompletes(bool uploadFailure, bool corruption)
    {
        using var drive = new Drive { FailUpload = uploadFailure, Corrupt = corruption }; using var http = new HttpClient(drive);
        var storage = new GoogleDriveBackupStorage(new(http, Credentials)); var file = Path.Combine(directory, "file.zip");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        if (uploadFailure) await Assert.ThrowsAsync<IOException>(() => storage.StoreAsync(file, Job));
        else await Assert.ThrowsAsync<InvalidDataException>(() => storage.StoreAsync(file, Job));
        Assert.True(drive.Deleted); Assert.False(drive.Complete);
    }
    [Fact]
    public async Task ResumableUploadCannotSendBearerToForeignHost()
    {
        using var drive = new Drive { SessionHost = "attacker.invalid" }; using var http = new HttpClient(drive);
        var storage = new GoogleDriveBackupStorage(new(http, Credentials)); var file = Path.Combine(directory, "file.zip");
        await File.WriteAllBytesAsync(file, [1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.StoreAsync(file, Job));
        Assert.Equal(0, drive.Chunks); Assert.True(drive.Deleted);
    }
    [Fact]
    public async Task UploadSessionCannotOverwriteAnotherGoogleFile()
    {
        using var drive = new Drive { SessionFile = "ForeignFile" }; using var http = new HttpClient(drive);
        var storage = new GoogleDriveBackupStorage(new(http, Credentials)); var file = Path.Combine(directory, "file.zip");
        await File.WriteAllBytesAsync(file, [1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.StoreAsync(file, Job));
        Assert.Equal(0, drive.Chunks); Assert.True(drive.Deleted);
    }
    [Fact]
    public async Task CancelledUploadUsesIndependentCleanupAndNeverCompletes()
    {
        using var cancellation = new CancellationTokenSource();
        using var drive = new Drive { CancelUpload = cancellation }; using var http = new HttpClient(drive);
        var storage = new GoogleDriveBackupStorage(new(http, Credentials)); var file = Path.Combine(directory, "file.zip");
        await File.WriteAllBytesAsync(file, [1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.StoreAsync(file, Job, cancellation.Token));
        Assert.True(drive.Deleted); Assert.False(drive.Complete);
    }
    [Fact]
    public async Task ChangedParentAndForeignJobPreventReadingAndDeletion()
    {
        var job = Job; using var drive = new Drive { Owner = Guid.NewGuid().ToString("N"), Bytes = [1] }; using var http = new HttpClient(drive);
        var storage = new GoogleDriveBackupStorage(new(http, Credentials));
        var record = new StoredBackup("gdrive:Folder_A/File_1", 1, Convert.ToHexString(SHA256.HashData(drive.Bytes)));
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.DeleteAsync(record, job)); Assert.False(drive.Deleted);
        drive.Owner = job.Id.ToString("N"); drive.Foreign = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.VerifyAsync(record, job)); Assert.False(drive.Deleted);
        drive.Foreign = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.DeleteAsync(record with { Location = "gdrive:ForeignFolder/File_1" }, job));
    }
    [Fact]
    public void DriveDestinationKeepsOwnRetentionAndRejectsDuplicates()
    {
        var job = Job with { AdditionalDestinations = [new() { StorageKind = StorageKind.LocalDirectory, DestinationDirectory = directory, KeepLast = 11 }] };
        Assert.Empty(job.Validate()); Assert.Equal(11, job.StorageJobs[1].KeepLast); Assert.Null(job.StorageJobs[1].GoogleDrive);
        var duplicate = job with { AdditionalDestinations = [new() { StorageKind = StorageKind.GoogleDrive,
            DestinationDirectory = job.DestinationDirectory, GoogleDrive = job.GoogleDrive, KeepLast = 2 }] };
        Assert.Contains(duplicate.Validate(), error => error.Contains("несколько раз"));
        var json = JsonSerializer.Serialize(job); Assert.DoesNotContain("fixture-refresh", json); Assert.DoesNotContain("fixture-secret", json); Assert.DoesNotContain("ClientSecret", json); Assert.DoesNotContain("RefreshToken", json);
        Assert.Equal(StorageKind.GoogleDrive, JsonSerializer.Deserialize<BackupJob>(json)!.StorageKind);
    }
    [Theory]
    [InlineData("../Folder")] [InlineData("https://drive.google.com/folders/Folder")] [InlineData("")]
    public void InvalidFolderIsRejected(string folder) => Assert.NotEmpty((Job with { DestinationDirectory = folder }).Validate());
    [Fact]
    public void DesktopClientImportRejectsServiceAccountAndMalformedJson()
    {
        Assert.Throws<InvalidDataException>(() => GoogleDriveAuthorization.ParseDesktopClient("{\"type\":\"service_account\"}"));
        Assert.Throws<InvalidDataException>(() => GoogleDriveAuthorization.ParseDesktopClient("{invalid"));
        var client = GoogleDriveAuthorization.ParseDesktopClient("{\"installed\":{\"client_id\":\"fixture.apps.googleusercontent.com\",\"client_secret\":\"secret\"}}");
        Assert.Equal("fixture.apps.googleusercontent.com", client.ClientId);
    }
}
