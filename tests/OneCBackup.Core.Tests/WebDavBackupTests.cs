using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using System.IO.Compression;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class WebDavBackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onec-webdav-" + Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private readonly DavFixture dav = new();
    private readonly BackupCatalog catalog;
    private readonly FileBackupStorage storage = new();
    private sealed class Unused : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new Exception("Not a database job"); }
    private sealed class Passwords : IArchivePasswordStore
    { public Task<string> GetAsync(string key, CancellationToken token = default) => Task.FromResult("AES архив password"); }
    public WebDavBackupTests() { Directory.CreateDirectory(root); catalog = new(Path.Combine(root, "copies.json")); }
    private BackupJob Job => new() { Id = id, Name = "DAV", Kind = DatabaseKind.WebDav, Source = "https://dav.example/data/",
        WebDav = new() { CredentialKey = "dav-binding" }, DestinationDirectory = Path.Combine(root, "copies"),
        TemporaryDirectory = Path.Combine(root, "hdd"), ScheduleKind = ScheduleKind.Manual, KeepLast = 1,
        FileBackupMode = FileBackupMode.Incremental, FullBackupEvery = 3 };
    private WebDavClient Client(BackupJob job) => new(job.Source, new NetworkCredential("пользователь", "app-secret"), dav.Handler());
    private LocalBackupRunner Runner(Action<BackupProgressSnapshot>? progress = null) => new(new Unused(), new BackupCompletion(storage, catalog, new Passwords()),
        new BackupRunHistory(Path.Combine(root, "runs.json")), Path.Combine(root, "work"), Client, progress: progress);
    private async Task<BackupRecord> Run(BackupJob? job = null)
    {
        var result = await Runner().RunAsync(job ?? Job); Assert.True(result.Status == BackupRunStatus.Succeeded, result.Error);
        Assert.False(Directory.Exists(result.WorkDirectory)); return (await catalog.ReadAsync()).Single(record => record.Backup == result.Backup);
    }
    private async Task<string> Restore(BackupRecord record, BackupJob? job = null)
    {
        var parent = Path.Combine(root, "restores"); Directory.CreateDirectory(parent);
        return (await new FileBackupRestorer(storage, catalog, Path.Combine(root, "work"), new Passwords())
            .RestoreAsync(record, job ?? Job, parent)).Directory;
    }
    [Fact]
    public async Task WholeSourceByteProgressDoesNotSwitchStagesForEachWebDavFile()
    {
        dav.Files["a.txt"] = (new string('a', 100), "\"a1\"");
        dav.Files["b.txt"] = (new string('b', 900), "\"b1\"");
        var events = new List<BackupProgressSnapshot>();
        for (var run = 0; run < 2; run++)
        {
            events.Clear();
            var result = await Runner(events.Add).RunAsync(Job);
            Assert.Equal(BackupRunStatus.Succeeded, result.Status);
            Assert.DoesNotContain(events, value => value.Progress.Stage is BackupStage.ArchivingFile or BackupStage.DownloadingFile or BackupStage.ProcessingFiles);
            var batch = events.Where(value => value.Progress.Stage == BackupStage.DownloadingAndArchivingFiles).ToArray();
            Assert.NotEmpty(batch); Assert.All(batch, value => Assert.Equal(1000, value.Progress.Total));
            Assert.Equal(1000, batch[^1].Progress.Completed);
            Assert.Equal(batch.Select(value => value.Progress.Completed).Order(), batch.Select(value => value.Progress.Completed));
        }
    }
    [Fact]
    public async Task FullIncrementalDeletionUnicodeEmptyFoldersAndIndependentRetentionRestore()
    {
        dav.Files["stable.txt"] = ("stable", "\"s1\""); dav.Files["папка/данные #%.txt"] = ("before", "\"c1\"");
        dav.Files["delete.txt"] = ("delete", "\"d1\""); dav.Directories.UnionWith(["папка", "empty"]);
        var job = Job with { AdditionalDestinations = [new() { DestinationDirectory = Path.Combine(root, "second"), KeepLast = 4 }] };
        var full = await Run(job); Assert.True(full.Backup.FileSnapshot!.IsFull); Assert.Equal(3, dav.Downloads.Count);
        dav.Downloads.Clear(); dav.Files.Remove("delete.txt"); dav.Files["папка/данные #%.txt"] = ("after!", "\"c2\""); dav.Files["new.txt"] = ("new", "\"n1\"");
        var inc = await Run(job); Assert.Equal(1, inc.Backup.FileSnapshot!.Depth); Assert.Equal(2, dav.Downloads.Count);
        Assert.DoesNotContain("stable.txt", dav.Downloads);
        using (var zip = ZipFile.OpenRead(inc.Backup.Location)) Assert.DoesNotContain(zip.Entries, entry => entry.FullName == "data/stable.txt");
        var restored = await Restore(inc, job);
        Assert.Equal("after!", await File.ReadAllTextAsync(Path.Combine(restored, "папка", "данные #%.txt")));
        Assert.Equal("stable", await File.ReadAllTextAsync(Path.Combine(restored, "stable.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(restored, "new.txt")));
        Assert.False(File.Exists(Path.Combine(restored, "delete.txt"))); Assert.True(Directory.Exists(Path.Combine(restored, "empty")));
        Assert.Equal(4, (await catalog.ReadAsync()).Count);
        await Run(job); var fresh = await Run(job); Assert.True(fresh.Backup.FileSnapshot!.IsFull);
        var records = await catalog.ReadAsync(); Assert.Single(records, record => record.StorageIdentity == full.StorageIdentity);
        Assert.Equal(4, records.Count(record => record.StorageIdentity != full.StorageIdentity));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    public async Task MissingOrWeakETagAlwaysDownloadsAndDetectsSameSizeAndDateChange(string? etag)
    {
        dav.Files["a.txt"] = ("1111", etag); await Run(); dav.Downloads.Clear(); dav.Files["a.txt"] = ("2222", etag);
        var inc = await Run(); Assert.Single(dav.Downloads);
        Assert.Equal("2222", await File.ReadAllTextAsync(Path.Combine(await Restore(inc), "a.txt")));
        dav.Downloads.Clear(); var unchanged = await Run(); Assert.Single(dav.Downloads);
        using var zip = ZipFile.OpenRead(unchanged.Backup.Location); Assert.Single(zip.Entries);
    }
    [Fact]
    public async Task NoChangeStrongETagSkipsDownloadsAndEncryptedChainRestores()
    {
        dav.Files["a.txt"] = ("secret", "\"a1\""); var job = Job with { ArchivePasswordKey = "archive-binding" };
        await Run(job); dav.Downloads.Clear(); var inc = await Run(job); Assert.Empty(dav.Downloads);
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(inc.Backup.Location))
            foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry entry in zip) Assert.Equal(256, entry.AESKeySize);
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(await Restore(inc, job), "a.txt")));
    }
    [Theory]
    [InlineData("denied")]
    [InlineData("missing-self")]
    [InlineData("foreign")]
    [InlineData("traversal")]
    [InlineData("encoded-slash")]
    [InlineData("duplicate")]
    [InlineData("dtd")]
    [InlineData("redirect")]
    [InlineData("truncated")]
    [InlineData("etag-mismatch")]
    [InlineData("precondition")]
    [InlineData("changed-after")]
    public async Task IncompleteOrChangingSourceNeverRegistersOrDeletesPreviousCopy(string failure)
    {
        dav.Files["a.txt"] = ("old", "\"a1\""); var full = await Run();
        var before = JsonSerializer.Serialize(await catalog.ReadAsync()); var index = Path.Combine(root, "file-snapshots", id.ToString("N") + ".json");
        var indexBytes = await File.ReadAllBytesAsync(index); dav.Files["a.txt"] = ("new", "\"a2\""); dav.Failure = failure;
        var result = await Runner().RunAsync(Job); Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync())); Assert.True(File.Exists(full.Backup.Location));
        Assert.Equal(indexBytes, await File.ReadAllBytesAsync(index));
        Assert.Empty(Directory.GetFiles(result.WorkDirectory, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(result.WorkDirectory, "*.webdav-partial", SearchOption.AllDirectories));
        Assert.DoesNotContain("app-secret", result.Error ?? "");
    }
    [Fact]
    public async Task CancelledDownloadCleansStageAndLeavesCatalogUntouched()
    {
        dav.Files["a.txt"] = ("old", "\"a1\""); await Run(); var before = JsonSerializer.Serialize(await catalog.ReadAsync());
        dav.Files["a.txt"] = ("new", "\"a2\""); using var cancellation = new CancellationTokenSource();
        dav.OnDownload = () => cancellation.Cancel();
        var run = await Runner().RunAsync(Job, cancellation.Token); Assert.Equal(BackupRunStatus.Cancelled, run.Status);
        Assert.Equal(before, JsonSerializer.Serialize(await catalog.ReadAsync()));
        Assert.Empty(Directory.GetFiles(run.WorkDirectory, "*.partial", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task SourceCheckReadsOnlyListingsAndNewAccountForcesFull()
    {
        dav.Files["a.txt"] = ("data", "\"a1\"");
        var check = await new SourceReadinessChecker(new BackupProcessRunner(), root, webDavClientFactory: Client).CheckAsync(Job);
        Assert.True(check.Succeeded, check.Details); Assert.Empty(dav.Downloads); Assert.Empty(await catalog.ReadAsync());
        await Run(); var other = new LocalBackupRunner(new Unused(), new BackupCompletion(storage, catalog), new BackupRunHistory(Path.Combine(root, "runs.json")),
            Path.Combine(root, "work"), job => new(job.Source, new NetworkCredential("other", "secret"), dav.Handler()));
        var result = await other.RunAsync(Job); Assert.Equal(BackupRunStatus.Succeeded, result.Status); Assert.True(result.Backup!.FileSnapshot!.IsFull);
    }
    [Theory]
    [InlineData("http://dav.example/data/")]
    [InlineData("https://user:secret@dav.example/data/")]
    [InlineData("https://dav.example/data/?token=secret")]
    [InlineData("https://dav.example/data/#fragment")]
    public void UnsafeSourceCannotBeConfigured(string source) => Assert.NotEmpty((Job with { Source = source }).Validate());
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    internal sealed class DavFixture
    {
        internal readonly Dictionary<string, (string Content, string? ETag)> Files = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Directories = new(StringComparer.Ordinal);
        internal readonly List<string> Downloads = [];
        internal string? Failure;
        internal Action? OnDownload;
        private int scans;
        internal HttpMessageHandler Handler() => new HandlerImpl(this);
        private sealed class HandlerImpl(DavFixture fixture) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => fixture.Send(request, token); }
        internal Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.NotNull(request.Headers.Authorization); Assert.Equal("Basic", request.Headers.Authorization.Scheme);
            var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath[6..]).TrimEnd('/');
            if (Failure == "redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://foreign.example/secret/") } });
            if (request.Method.Method == "PROPFIND")
            {
                Assert.Equal("1", request.Headers.GetValues("Depth").Single()); if (path == "") scans++;
                XNamespace d = "DAV:";
                string Href(string relative, bool directory) => "/data/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)) + (directory && relative != "" ? "/" : "");
                XElement Entry(string relative, bool directory)
                {
                    var props = new XElement(d + "prop", new XElement(d + "resourcetype", directory ? new XElement(d + "collection") : null));
                    if (!directory)
                    {
                        var file = Files[relative]; props.Add(new XElement(d + "getcontentlength", Encoding.UTF8.GetByteCount(file.Content)),
                            new XElement(d + "getlastmodified", "Mon, 01 Jun 2026 12:00:00 GMT"));
                        if (file.ETag is not null) props.Add(new XElement(d + "getetag", file.ETag));
                    }
                    return new XElement(d + "response", new XElement(d + "href", Href(relative, directory)),
                        new XElement(d + "propstat", props, new XElement(d + "status", Failure == "denied" && !directory ? "HTTP/1.1 403 Forbidden" : "HTTP/1.1 200 OK")));
                }
                var listing = new XElement(d + "multistatus"); if (Failure != "missing-self") listing.Add(Entry(path, true));
                static string Parent(string value) => value.Contains('/') ? value[..value.LastIndexOf('/')] : "";
                foreach (var directory in Directories.Where(value => Parent(value) == path)) listing.Add(Entry(directory, true));
                foreach (var file in Files.Keys.Where(value => Parent(value) == path)) listing.Add(Entry(file, false));
                if (Failure == "changed-after" && scans % 2 == 0) listing.Add(new XElement(d + "response", new XElement(d + "href", "/data/new-empty/"),
                    new XElement(d + "propstat", new XElement(d + "prop", new XElement(d + "resourcetype", new XElement(d + "collection"))), new XElement(d + "status", "HTTP/1.1 200 OK"))));
                if (Failure == "duplicate") listing.Add(Entry(path, true));
                var target = listing.Elements().LastOrDefault();
                if (target is not null && Failure is "foreign" or "traversal" or "encoded-slash") target.Element(d + "href")!.Value = Failure switch
                { "foreign" => "https://foreign.example/data/a.txt", "traversal" => "/data/%2e%2e/secret.txt", _ => "/data/folder%2fa.txt" };
                var text = listing.ToString(); if (Failure == "dtd") text = "<!DOCTYPE x [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]>" + text;
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)207) { Content = new StringContent(text, Encoding.UTF8, "application/xml") });
            }
            Assert.Equal(HttpMethod.Get, request.Method); Downloads.Add(path); OnDownload?.Invoke(); token.ThrowIfCancellationRequested();
            var value = Files[path]; if (WebDavClient.IsStrongETag(value.ETag)) Assert.Equal(value.ETag, request.Headers.IfMatch.Single().ToString());
            if (Failure == "precondition") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
            var bytes = Encoding.UTF8.GetBytes(value.Content); if (Failure == "truncated") bytes = bytes[..^1];
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            if (value.ETag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(Failure == "etag-mismatch" ? "\"different\"" : value.ETag);
            return Task.FromResult(response);
        }
    }
}
