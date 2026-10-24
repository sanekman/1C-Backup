using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class DiagnosticTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onec-diagnostics-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(root, "profile");
    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private sealed class NeverDatabase : IBackupExecutor
    { public Task<string> CreateAsync(BackupJob job, string work, CancellationToken token) => throw new Exception("Unexpected DB executor"); }
    public DiagnosticTests() => Directory.CreateDirectory(Profile);
    private DiagnosticLog Logger(DiagnosticPolicy? policy = null) => new(Profile, DiagnosticComponent.Tests, "0.1.0-test", "abcdef", policy);
    private IReadOnlyList<DiagnosticEntry> Read() => Directory.GetFiles(Path.Combine(Profile, "logs"), "*.jsonl")
        .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<DiagnosticEntry>(line, Json)!).ToArray();
    private BackupJob Job => new() { Name = "do-not-export-job-name", Source = Path.Combine(root, "private-source"), Kind = DatabaseKind.FilesAndFolders,
        DestinationDirectory = Path.Combine(root, "private-copies"), ScheduleKind = ScheduleKind.Manual };
    private LocalBackupRunner Runner(DiagnosticLog log) => new(new NeverDatabase(), new BackupCompletion(new FileBackupStorage(), new(Path.Combine(Profile, "copies.json"))),
        new(Path.Combine(Profile, "runs.json")), Path.Combine(Profile, "work"), diagnosticLog: log);
    private async Task<string> Bundle(DiagnosticLog log, string name = "diagnostics.zip")
    {
        using var scope = DiagnosticLog.Use(log);
        var result = await new DiagnosticBundle(Profile, "0.1.0-test", "abcdef").ExportAsync(Path.Combine(root, name));
        return result.Path;
    }
    [Fact]
    public async Task FailureDetailsExcludeSecretsFromNestedExceptionsDataPathsAndHistory()
    {
        var secret = "secret-DO-NOT-SHARE-password";
        var log = Logger(); Exception error;
        try { throw new AggregateException("https://user:" + secret + "@host/?token=" + secret, new IOException("Password=" + secret)); }
        catch (Exception ex) { error = ex; }
        error.Data["access_token"] = secret;
        log.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, error);
        await new BackupRunHistory(Path.Combine(Profile, "runs.json")).SaveAsync(new(Guid.NewGuid(), Guid.NewGuid(), secret, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, BackupRunStatus.Failed, Path.Combine(root, secret), Error: "Password=" + secret, Warnings: [secret]));
        await File.WriteAllTextAsync(Path.Combine(Profile, "jobs.json"), "{\"password\":\"" + secret + "\"}");
        await File.WriteAllTextAsync(Path.Combine(Profile, "raw-process.log"), secret);
        using var zip = ZipFile.OpenRead(await Bundle(log));
        Assert.Contains(zip.Entries, entry => entry.FullName == "runs-summary.json");
        Assert.DoesNotContain(zip.Entries, entry => entry.Name is "jobs.json" or "raw-process.log" or "runs.json");
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open()); var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain(secret, text); Assert.DoesNotContain(root, text); Assert.DoesNotContain("https://user", text);
        }
        var captured = Read().Single(entry => entry.Event == DiagnosticEvent.DesktopError);
        Assert.Equal(typeof(AggregateException).FullName, captured.Error!.Type); Assert.Single(captured.Error.Inner);
        Assert.NotEmpty(captured.Error.Frames); Assert.Equal(error.HResult, captured.Error.HResult);
    }
    [Fact]
    public async Task SuccessfulRunLogsCorrelatedStagesAndIndependentDestinationFailureWithoutRawPaths()
    {
        var job = Job; Directory.CreateDirectory(job.Source); await File.WriteAllTextAsync(Path.Combine(job.Source, "private-file.txt"), "private payload");
        var blocked = Path.Combine(root, "blocked"); await File.WriteAllTextAsync(blocked, "keep");
        job = job with { AdditionalDestinations = [new() { DestinationDirectory = blocked }] };
        var run = await Runner(Logger()).RunAsync(job); Assert.Equal(BackupRunStatus.PartiallySucceeded, run.Status);
        var entries = Read().Where(entry => entry.RunId == run.RunId).ToArray(); Assert.NotEmpty(entries);
        Assert.All(entries, entry => { Assert.Equal(job.Id, entry.JobId); Assert.Equal(DatabaseKind.FilesAndFolders, entry.SourceKind); });
        Assert.Equal(DiagnosticEvent.RunStarted, entries.First().Event); Assert.Equal(DiagnosticEvent.RunFinished, entries.Last().Event);
        Assert.Contains(entries, entry => entry.Event == DiagnosticEvent.BackupCreationCompleted && entry.Values!.Bytes > 0);
        Assert.Contains(entries, entry => entry.Event == DiagnosticEvent.DestinationRegistered && entry.DestinationIndex == 1);
        Assert.Contains(entries, entry => entry.Event == DiagnosticEvent.DestinationFailed && entry.DestinationIndex == 2 && entry.Error is not null);
        Assert.Contains(entries, entry => entry.Event == DiagnosticEvent.RetentionCompleted);
        Assert.Equal(BackupRunStatus.PartiallySucceeded, entries.Last().Outcome);
        var text = string.Join('\n', Directory.GetFiles(Path.Combine(Profile, "logs")).Select(File.ReadAllText));
        Assert.DoesNotContain(job.Name, text); Assert.DoesNotContain(job.Source, text); Assert.DoesNotContain("private payload", text);
    }
    [Fact]
    public async Task UnwritableLoggerDoesNotChangeSuccessfulBackupAndSupportReportsFailure()
    {
        await File.WriteAllTextAsync(Path.Combine(Profile, "logs"), "unrelated file");
        var log = Logger(); var job = Job; Directory.CreateDirectory(job.Source); await File.WriteAllTextAsync(Path.Combine(job.Source, "a.txt"), "data");
        var result = await Runner(log).RunAsync(job); Assert.Equal(BackupRunStatus.Succeeded, result.Status); Assert.NotNull(log.LastWriteFailure);
        using var zip = ZipFile.OpenRead(await Bundle(log)); using var reader = new StreamReader(zip.GetEntry("support-info.json")!.Open());
        using var info = JsonDocument.Parse(await reader.ReadToEndAsync()); Assert.NotEqual(JsonValueKind.Null, info.RootElement.GetProperty("LoggingFailure").ValueKind);
        Assert.Equal("unrelated file", await File.ReadAllTextAsync(Path.Combine(Profile, "logs")));
    }
    [Fact]
    public void RotationAgeAndTotalQuotaPreserveForeignFilesAndNewestEvents()
    {
        var directory = Path.Combine(Profile, "logs"); Directory.CreateDirectory(directory);
        var old = Path.Combine(directory, "Agent-20260101-1-" + Guid.NewGuid().ToString("N") + "-0000.jsonl");
        File.WriteAllText(old, "old"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-20));
        var foreign = Path.Combine(directory, "customer.jsonl"); File.WriteAllText(foreign, "keep");
        var log = Logger(new(MaximumFileBytes: 4096, MaximumTotalBytes: 8192));
        for (var count = 0; count < 200; count++) log.Write(DiagnosticEvent.RunFinished, values: new(Code: count));
        Assert.Null(log.LastWriteFailure); Assert.False(File.Exists(old)); Assert.Equal("keep", File.ReadAllText(foreign));
        var owned = Directory.GetFiles(directory).Where(path => Path.GetFileName(path).StartsWith("Tests-")).Select(path => new FileInfo(path)).ToArray();
        Assert.True(owned.Length > 0); Assert.All(owned, file => Assert.InRange(file.Length, 1, 4096)); Assert.True(owned.Sum(file => file.Length) <= 8192);
        Assert.Contains(owned.SelectMany(file => File.ReadAllLines(file.FullName)), line => JsonSerializer.Deserialize<DiagnosticEntry>(line, Json)!.Values?.Code == 199);
    }
    [Fact]
    public async Task BundleSkipsMalformedIncompleteUnknownAndForeignFilesWhileOtherLogsRemainReadable()
    {
        var log = Logger(); log.Write(DiagnosticEvent.ProcessStarted);
        var directory = Path.Combine(Profile, "logs");
        var name = "Agent-20260101-1-" + Guid.NewGuid().ToString("N") + "-0000.jsonl";
        await File.WriteAllTextAsync(Path.Combine(directory, name), "{\"Password\":\"do-not-copy-secret\"}\n{unfinished");
        await File.WriteAllBytesAsync(Path.Combine(directory, "Agent-20260101-2-" + Guid.NewGuid().ToString("N") + "-0000.jsonl"), [255, 255, 10]);
        await File.WriteAllTextAsync(Path.Combine(directory, "foreign.jsonl"), "do-not-copy-secret");
        using var zip = ZipFile.OpenRead(await Bundle(log));
        foreach (var entry in zip.Entries) { using var reader = new StreamReader(entry.Open()); Assert.DoesNotContain("do-not-copy-secret", await reader.ReadToEndAsync()); }
        using var infoReader = new StreamReader(zip.GetEntry("support-info.json")!.Open()); using var info = JsonDocument.Parse(await infoReader.ReadToEndAsync());
        Assert.True(info.RootElement.GetProperty("Skipped").GetInt32() >= 3); Assert.True(info.RootElement.GetProperty("Events").GetInt32() > 0);
    }
    [Fact]
    public async Task ExportNeverOverwritesExistingFileOrWritesIntoProfileAndCancellationCleansOwnStage()
    {
        var target = Path.Combine(root, "existing.zip"); await File.WriteAllTextAsync(target, "keep");
        var bundle = new DiagnosticBundle(Profile, "test", "abcdef");
        await Assert.ThrowsAsync<IOException>(() => bundle.ExportAsync(target)); Assert.Equal("keep", await File.ReadAllTextAsync(target));
        await Assert.ThrowsAsync<InvalidDataException>(() => bundle.ExportAsync(Path.Combine(Profile, "bad.zip")));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bundle.ExportAsync(Path.Combine(root, "cancelled.zip"), cancellation.Token));
        Assert.False(File.Exists(Path.Combine(root, "cancelled.zip"))); Assert.Empty(Directory.GetFiles(root, "*.partial"));
    }
    [Fact]
    public async Task CorruptHistoryDoesNotPreventDiagnosticExportOrCopyRawCorruption()
    {
        await File.WriteAllTextAsync(Path.Combine(Profile, "runs.json"), "invalid history containing secret-token");
        using var zip = ZipFile.OpenRead(await Bundle(Logger())); using var reader = new StreamReader(zip.GetEntry("support-info.json")!.Open());
        using var info = JsonDocument.Parse(await reader.ReadToEndAsync()); Assert.Equal("Unavailable", info.RootElement.GetProperty("HistoryStatus").GetString());
        Assert.DoesNotContain(zip.Entries, entry => entry.Name == "runs.json");
    }
    [Fact]
    public async Task ParallelAmbientScopesPreserveRunBindingsAndWholeJsonLines()
    {
        var log = Logger(); var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        await Task.WhenAll(ids.Select(id => Task.Run(async () =>
        {
            using var scope = DiagnosticLog.Use(log, id, id, DatabaseKind.WebDav);
            await Task.Yield(); for (var i = 0; i < 20; i++) DiagnosticLog.Current.Write(DiagnosticEvent.RunStarted);
        })));
        var entries = Read(); Assert.Equal(160, entries.Count);
        Assert.All(entries, entry => { Assert.Equal(entry.JobId, entry.RunId); Assert.Contains(entry.JobId!.Value, ids); });
        Assert.All(ids, id => Assert.Equal(20, entries.Count(entry => entry.RunId == id)));
    }
    private sealed class FailedWebDav(bool networkFailure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => networkFailure ? throw new HttpRequestException("https://private-server.example/secret-token; password=do-not-export")
                : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebDavFailureKeepsHttpCodeOrNetworkTypeWithoutAddressAndCredentials(bool networkFailure)
    {
        var log = Logger();
        using (DiagnosticLog.Use(log, Guid.NewGuid(), Guid.NewGuid(), DatabaseKind.WebDav))
        using (var client = new WebDavClient("https://private-server.example/secret-token/", new System.Net.NetworkCredential("user", "do-not-export"), new FailedWebDav(networkFailure)))
            await Assert.ThrowsAsync<IOException>(() => client.ScanAsync());
        var entry = Assert.Single(Read(), entry => entry.Event == DiagnosticEvent.WebDavRequestFailed);
        if (networkFailure) Assert.Equal(typeof(HttpRequestException).FullName, entry.Error!.Type);
        else Assert.Equal(401, entry.Values!.Code);
        using var zip = ZipFile.OpenRead(await Bundle(log));
        foreach (var item in zip.Entries)
        {
            using var reader = new StreamReader(item.Open()); var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain("private-server", text); Assert.DoesNotContain("secret-token", text); Assert.DoesNotContain("do-not-export", text);
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
