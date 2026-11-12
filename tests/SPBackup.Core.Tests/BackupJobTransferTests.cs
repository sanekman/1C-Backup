using System.Text.Json;
using System.Text.Json.Nodes;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupJobTransferTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-job-transfer-" + Guid.NewGuid());
    public BackupJobTransferTests() => Directory.CreateDirectory(root);
    private BackupJob Job(DatabaseKind kind = DatabaseKind.OneCFile) => new()
    {
        Id = Guid.NewGuid(), Name = "Бухгалтерия", Kind = kind, Source = kind == DatabaseKind.WebDav ? "https://dav.example/data/" : kind is DatabaseKind.OneCFile or DatabaseKind.FilesAndFolders ? root : "database",
        WebDav = kind == DatabaseKind.WebDav ? new() { CredentialKey = "dav-binding", TimeoutMinutes = 45 } : null,
        FileBackupMode = FileBackupMode.Incremental, FullBackupEvery = 12, FullBackupDayOfWeek = DayOfWeek.Friday,
        DestinationDirectory = Path.Combine(root, "copies"), TemporaryDirectory = Path.Combine(root, "work"),
        ArchiveCopies = true, ArchivePasswordKey = "archive-binding", Enabled = true, KeepLast = 7,
        ScheduleKind = ScheduleKind.Weekly, DaysOfWeek = WeekDays.Monday | WeekDays.Friday,
        DailyAt = new(8, 0), AdditionalDailyTimes = [new(16, 0), new(23, 0)], TimeZoneId = "UTC",
        AdditionalDestinations = [new() { StorageKind = StorageKind.Ftp, DestinationDirectory = "/backups", KeepLast = 2,
            Ftp = new() { Host = "backup.example", CredentialKey = "ftp-binding", UseTls = true } }],
        OneC = kind == DatabaseKind.OneCFile ? new() { ExecutablePath = Path.Combine(root, "1cv8.exe"), TimeoutMinutes = 90 } : null,
        SqlServer = kind == DatabaseKind.SqlServer ? new() { Server = "SQL-SERVER", ServerBackupDirectory = root,
            AgentReadDirectory = Path.Combine(root, "sql-read"), TimeoutMinutes = 60 } : null,
        PostgreSql = kind == DatabaseKind.PostgreSql ? new() { Host = "pg-server", Port = 5433, Username = "backup",
            PgDumpPath = Path.Combine(root, "pg_dump.exe"), PgRestorePath = Path.Combine(root, "pg_restore.exe"), TimeoutMinutes = 45 } : null
    };

    [Theory]
    [InlineData(DatabaseKind.OneCFile)]
    [InlineData(DatabaseKind.SqlServer)]
    [InlineData(DatabaseKind.PostgreSql)]
    [InlineData(DatabaseKind.FilesAndFolders)]
    [InlineData(DatabaseKind.WebDav)]
    public async Task RoundTripKeepsAllSettingsAndBindingsButCreatesNewDisabledIdentity(DatabaseKind kind)
    {
        var job = Job(kind); var path = Path.Combine(root, "settings.json");
        await BackupJobTransfer.ExportAsync(job, path);
        var imported = await BackupJobTransfer.ImportAsync(path);
        var again = await BackupJobTransfer.ImportAsync(path);
        Assert.NotEqual(job.Id, imported.Id); Assert.NotEqual(imported.Id, again.Id);
        Assert.False(imported.Enabled); Assert.False(again.Enabled); Assert.True(job.Enabled);
        Assert.Equal(JsonSerializer.Serialize(job with { Id = imported.Id, Enabled = false }), JsonSerializer.Serialize(imported));
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("archive-binding", data.RootElement.GetProperty("Job").GetProperty("ArchivePasswordKey").GetString());
        Assert.Equal("ftp-binding", imported.AdditionalDestinations![0].Ftp!.CredentialKey);
        Assert.False(data.RootElement.GetProperty("Job").TryGetProperty("Password", out _));
        Assert.Equal(new[] { path }, Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingTargetIsNeverOverwritten(bool directory)
    {
        var path = Path.Combine(root, "existing");
        if (directory) Directory.CreateDirectory(path); else await File.WriteAllTextAsync(path, "keep");
        await Assert.ThrowsAsync<IOException>(() => BackupJobTransfer.ExportAsync(Job(), path));
        if (!directory) Assert.Equal("keep", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("application")]
    [InlineData("missing-version")]
    [InlineData("missing-id")]
    [InlineData("unknown-secret")]
    [InlineData("invalid-schedule")]
    [InlineData("duplicate-storage")]
    public async Task InvalidDocumentCannotProduceAnImportableJob(string change)
    {
        var path = Path.Combine(root, "settings.json"); var job = Job();
        await BackupJobTransfer.ExportAsync(job, path);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var source = document["Job"]!.AsObject();
        switch (change)
        {
            case "version": document["FormatVersion"] = 2; break;
            case "application": document["Application"] = "other"; break;
            case "missing-version": document.Remove("FormatVersion"); break;
            case "missing-id": source.Remove("Id"); break;
            case "unknown-secret": source["Password"] = "not-a-supported-field"; break;
            case "invalid-schedule": source["ScheduleKind"] = 999; break;
            default: source["AdditionalDestinations"] = JsonSerializer.SerializeToNode(new[] { new BackupDestination { DestinationDirectory = job.DestinationDirectory } }); break;
        }
        var before = document.ToJsonString(); await File.WriteAllTextAsync(path, before);
        var exception = await Record.ExceptionAsync(() => BackupJobTransfer.ImportAsync(path));
        Assert.True(exception is JsonException or InvalidDataException, "Invalid metadata was accepted or failed unexpectedly.");
        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BackupJobTransfer.MaximumBytes + 1)]
    public async Task EmptyOrOversizedInputIsRejectedBeforeParsing(int size)
    {
        var path = Path.Combine(root, "input.json"); await File.WriteAllBytesAsync(path, new byte[size]);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupJobTransfer.ImportAsync(path));
    }

    [Fact]
    public async Task CancelledExportAndImportLeaveFilesUnchanged()
    {
        var path = Path.Combine(root, "settings.json"); await BackupJobTransfer.ExportAsync(Job(), path);
        var before = await File.ReadAllTextAsync(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var other = Path.Combine(root, "cancelled.json");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupJobTransfer.ExportAsync(Job(), other, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupJobTransfer.ImportAsync(path, cancellation.Token));
        Assert.False(File.Exists(other)); Assert.Empty(Directory.GetFiles(root, "*.partial"));
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }
    public void Dispose() => Directory.Delete(root, true);
}
