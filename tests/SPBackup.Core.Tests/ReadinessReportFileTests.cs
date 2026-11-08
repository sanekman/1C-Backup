using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class ReadinessReportFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "spbackup-report-" + Guid.NewGuid().ToString("N"));
    public ReadinessReportFileTests() => Directory.CreateDirectory(root);
    [Fact]
    public async Task WritesUnicodeAndNeverReplacesExistingFile()
    {
        var path = Path.Combine(root, "report.txt");
        await ReadinessReportFile.SaveNewAsync(path, "Проверка · пользователь\nВторая строка");
        var before = await File.ReadAllBytesAsync(path);
        Assert.Contains("Проверка", await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<IOException>(() => ReadinessReportFile.SaveNewAsync(path, "replacement"));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(root));
    }
    [Fact]
    public async Task CancelledWriteDoesNotPublishOrLeavePartialFile()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var path = Path.Combine(root, "report.txt");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadinessReportFile.SaveNewAsync(path, "text", cancelled.Token));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    [Fact]
    public async Task ConcurrentExportsPublishExactlyOneCompleteReport()
    {
        var path = Path.Combine(root, "report.txt");
        async Task<bool> Attempt(string text)
        { try { await ReadinessReportFile.SaveNewAsync(path, text); return true; } catch (IOException) { return false; } }
        var outcomes = await Task.WhenAll(Attempt(new string('A', 65536)), Attempt(new string('B', 65536)));
        Assert.Single(outcomes, value => value);
        var text = await File.ReadAllTextAsync(path);
        Assert.True(text == new string('A', 65536) || text == new string('B', 65536));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }
    public void Dispose() => Directory.Delete(root, true);
}
