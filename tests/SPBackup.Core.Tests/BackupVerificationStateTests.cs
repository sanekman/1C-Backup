using System.Text.Json;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class BackupVerificationStateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-checks-" + Guid.NewGuid());
    private string StatePath => Path.Combine(root, "checks.json");
    private static BackupRecord Record() => new(Guid.NewGuid(), Guid.NewGuid(), "LocalDisk:fixture", DateTimeOffset.UtcNow,
        new StoredBackup("fixture.zip", 42, new string('A', 64)));
    private static BackupVerificationEntry Entry(BackupRecord record, BackupVerificationOutcome outcome = BackupVerificationOutcome.Verified) =>
        BackupVerificationEntry.Create(record, outcome, BackupVerificationOrigin.Manual, DateTimeOffset.UtcNow);

    [Fact]
    public async Task PersistedChecksMatchOnlyExactIdentityAndFingerprint()
    {
        var record = Record();
        var state = new BackupVerificationState(StatePath);
        Assert.Empty(await state.ReadAsync());
        await state.RememberAsync([Entry(record)], [record]);
        var check = Assert.Single(await new BackupVerificationState(StatePath).ReadAsync());
        Assert.True(check.Matches(record));
        Assert.False(check.Matches(record with { Id = Guid.NewGuid() }));
        Assert.False(check.Matches(record with { JobId = Guid.NewGuid() }));
        Assert.False(check.Matches(record with { StorageIdentity = "another" }));
        Assert.False(check.Matches(record with { Backup = record.Backup with { Location = "another.zip" } }));
        Assert.False(check.Matches(record with { Backup = record.Backup with { Length = 43 } }));
        Assert.False(check.Matches(record with { Backup = record.Backup with { Sha256 = new string('B', 64) } }));
    }

    [Fact]
    public async Task NewFailureReplacesSuccessEvenWhenClockMovesBackwards()
    {
        var record = Record(); var state = new BackupVerificationState(StatePath);
        var success = Entry(record) with { CheckedUtc = DateTimeOffset.UtcNow.AddDays(1) };
        await state.RememberAsync([success], [record]);
        var failure = Entry(record, BackupVerificationOutcome.Failed) with { Error = "missing" };
        await state.RememberAsync([failure], [record]);
        Assert.Equal(failure, Assert.Single(await state.ReadAsync()));
        await state.RememberAsync([Entry(record, BackupVerificationOutcome.Cancelled)], [record]);
        Assert.Equal(BackupVerificationOutcome.Cancelled, Assert.Single(await state.ReadAsync()).Outcome);
    }

    [Fact]
    public async Task ConcurrentUpdatesMergeAndPruneOnlyUnregisteredDerivedEntries()
    {
        var one = Record(); var two = Record(); var state = new BackupVerificationState(StatePath);
        await Task.WhenAll(state.RememberAsync([Entry(one)], [one, two]), state.RememberAsync([Entry(two)], [one, two]));
        Assert.Equal(2, (await state.ReadAsync()).Count);
        Directory.CreateDirectory(root);
        var unrelated = Path.Combine(root, "backup.zip"); await File.WriteAllTextAsync(unrelated, "keep");
        await state.RememberAsync([Entry(two)], [two]);
        Assert.Equal(two.Id, Assert.Single(await state.ReadAsync()).BackupId);
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task ForeignResultCannotReplaceExistingState()
    {
        var record = Record(); var state = new BackupVerificationState(StatePath);
        await state.RememberAsync([Entry(record)], [record]); var before = await File.ReadAllTextAsync(StatePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => state.RememberAsync([Entry(Record())], [record]));
        Assert.Equal(before, await File.ReadAllTextAsync(StatePath));
    }

    [Theory]
    [InlineData("missing-outcome")]
    [InlineData("unknown-outcome")]
    [InlineData("duplicate")]
    [InlineData("version")]
    [InlineData("null")]
    public async Task CorruptStateIsNeverInterpretedAsSuccessOrOverwritten(string kind)
    {
        var record = Record(); Directory.CreateDirectory(root);
        var value = JsonSerializer.Serialize(new { Version = 1, Entries = new[] { Entry(record) } });
        value = kind switch
        {
            "missing-outcome" => value.Replace("\"Outcome\":0,", ""),
            "unknown-outcome" => value.Replace("\"Outcome\":0", "\"Outcome\":77"),
            "duplicate" => JsonSerializer.Serialize(new { Version = 1, Entries = new[] { Entry(record), Entry(record) } }),
            "version" => value.Replace("\"Version\":1", "\"Version\":2"),
            _ => "null"
        };
        await File.WriteAllTextAsync(StatePath, value);
        var state = new BackupVerificationState(StatePath);
        await Assert.ThrowsAnyAsync<Exception>(() => state.ReadAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => state.RememberAsync([Entry(record)], [record]));
        Assert.Equal(value, await File.ReadAllTextAsync(StatePath));
    }

    [Fact]
    public async Task CancellationPreservesExistingFileAndNoTemporaryFilesRemain()
    {
        var record = Record(); var state = new BackupVerificationState(StatePath);
        await state.RememberAsync([Entry(record)], [record]); var before = await File.ReadAllTextAsync(StatePath);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.RememberAsync([Entry(record, BackupVerificationOutcome.Failed)], [record], cancellation.Token));
        Assert.Equal(before, await File.ReadAllTextAsync(StatePath));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
