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
        for (var round = 0; round < 20; round++)
        {
            var path = Path.Combine(root, "report-" + round + ".txt");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> Attempt(char value)
            {
                await start.Task;
                try { await ReadinessReportFile.SaveNewAsync(path, new string(value, 65536)); return true; }
                catch (IOException) { return false; }
            }
            var attempts = new[] { Attempt('A'), Attempt('B'), Attempt('C'), Attempt('D') };
            start.SetResult(); var outcomes = await Task.WhenAll(attempts);
            Assert.Single(outcomes, value => value);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains(text, new[] { new string('A', 65536), new string('B', 65536), new string('C', 65536), new string('D', 65536) });
            Assert.Equal(round + 1, Directory.GetFileSystemEntries(root).Length);
        }
    }
    public void Dispose() => Directory.Delete(root, true);
}
