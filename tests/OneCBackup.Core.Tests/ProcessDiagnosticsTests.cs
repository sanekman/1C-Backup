using System.Diagnostics;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class ProcessDiagnosticsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OneCBackup-process-" + Guid.NewGuid());
    public ProcessDiagnosticsTests() => Directory.CreateDirectory(directory);

    private async Task<BackupProcess> Command(string script)
    {
        var path = Path.Combine(directory, "fixture.py");
        await File.WriteAllTextAsync(path, script);
        return new BackupProcess(OperatingSystem.IsWindows() ? "python" : "python3", [path], directory)
        { ErrorLogPath = Path.Combine(directory, "process.log"), DiscardStandardOutput = true };
    }

    [Fact]
    public async Task ConcurrentOutputIsDrainedAndErrorsArePreservedWithExitCode()
    {
        var command = await Command("import sys\nsys.stdout.write('output' * 100000)\nsys.stderr.write('fixture diagnostic\\n')\nsys.exit(7)\n");
        Assert.Equal(7, await new BackupProcessRunner().RunAsync(command, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("fixture diagnostic\n", (await File.ReadAllTextAsync(command.ErrorLogPath!)).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task LargeDiagnosticsAreCappedWithoutBlockingTheProcess()
    {
        var command = await Command("import sys\nsys.stdout.write('O' * (2 * 1024 * 1024))\nsys.stderr.write('E' * (2 * 1024 * 1024))\nsys.exit(3)\n");
        Assert.Equal(3, await new BackupProcessRunner().RunAsync(command, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
        var bytes = await File.ReadAllBytesAsync(command.ErrorLogPath!);
        Assert.InRange(bytes.Length, 1024 * 1024, 1024 * 1024 + 100);
        Assert.Equal((byte)'E', bytes[0]);
        Assert.Contains("truncated at 1 MiB", System.Text.Encoding.UTF8.GetString(bytes.AsSpan(1024 * 1024)));
    }

    [Fact]
    public async Task CancellationStopsTheOwnedProcessAndRetainsWrittenDiagnostics()
    {
        var command = await Command("import os, pathlib, sys, time\nsys.stderr.write('started\\n')\nsys.stderr.flush()\npathlib.Path('ready.tmp').write_text(str(os.getpid()))\npathlib.Path('ready.tmp').replace('ready')\ntime.sleep(60)\n");
        using var cancellation = new CancellationTokenSource();
        var execution = new BackupProcessRunner().RunAsync(command, cancellation.Token);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (!File.Exists(Path.Combine(directory, "ready")) || new FileInfo(command.ErrorLogPath!).Length == 0)
            {
                if (execution.IsCompleted) await execution;
                if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Fixture did not start");
                await Task.Delay(50);
            }
            var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "ready")));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            var exited = false;
            try { using var process = Process.GetProcessById(pid); exited = process.HasExited; }
            catch (ArgumentException) { exited = true; }
            Assert.True(exited);
            Assert.Contains("started", await File.ReadAllTextAsync(command.ErrorLogPath!));
        }
        finally
        {
            cancellation.Cancel();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ExistingLogIsNotOverwrittenAndExternalLogPathIsRejectedBeforeLaunch()
    {
        var command = await Command("from pathlib import Path\nPath('unexpected-start').write_text('started')\n");
        await File.WriteAllTextAsync(command.ErrorLogPath!, "previous diagnostic");
        await Assert.ThrowsAsync<IOException>(() => new BackupProcessRunner().RunAsync(command, CancellationToken.None));
        Assert.Equal("previous diagnostic", await File.ReadAllTextAsync(command.ErrorLogPath!));
        await Assert.ThrowsAsync<ArgumentException>(() => new BackupProcessRunner().RunAsync(
            command with { ErrorLogPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log") }, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(directory, "unexpected-start")));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
