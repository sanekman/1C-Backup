using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SPBackup.Core;
using Xunit;
using Xunit.Abstractions;

namespace SPBackup.Core.Tests;

public sealed class NativeSmbFactAttribute : FactAttribute
{
    public NativeSmbFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ONECBACKUP_NATIVE_SMB") != "1")
            Skip = "Opt-in isolated administrator Windows runner with its own temporary SMB share.";
    }
}

public sealed class NativeSmbIntegrationTests(ITestOutputHelper output)
{
    [NativeSmbFact]
    [Trait("Category", "NativeSmbIntegration")]
    [SupportedOSPlatform("windows")]
    public async Task ActualShareDisconnectRetriesOnlyDeliveryAndPreservesRegisteredCopies()
    {
        await using var share = await SmbFixture.CreateAsync();
        var job = new BackupJob { Name = "Native SMB delivery", Source = "ready fixture archive",
            StorageKind = StorageKind.NetworkShare, DestinationDirectory = share.UncPath,
            ScheduleKind = ScheduleKind.Manual, Enabled = false, KeepLast = 2 };
        var catalog = new BackupCatalog(Path.Combine(share.Root, "copies.json"));
        var previousBytes = RandomNumberGenerator.GetBytes(12345);
        var previousSource = Path.Combine(share.Root, "previous.zip");
        await File.WriteAllBytesAsync(previousSource, previousBytes);
        var previous = await new BackupCompletion(new FileBackupStorage(), catalog)
            .CompleteAsync(job, previousSource, Path.Combine(share.Root, "previous-stage"), DateTimeOffset.UtcNow.AddMinutes(-1));
        var sourceBytes = RandomNumberGenerator.GetBytes(230000);
        var source = Path.Combine(share.Root, "ready.zip");
        await File.WriteAllBytesAsync(source, sourceBytes);
        var paused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualError = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var retries = new List<int>();
        var stages = new List<BackupStage>();
        var ordinary = new FileStorageOperations();
        var operations = ordinary with
        {
            OpenTemporary = path => Interlocked.Increment(ref opens) == 1
                ? new PausedSmbFile(path, paused, resume.Task, error =>
                {
                    var code = FileStorageFailures.Code(error);
                    output.WriteLine("Actual SMB write failure: Win32={0}, HResult=0x{1:X8}", code, error.HResult);
                    actualError.TrySetResult(code);
                })
                : ordinary.OpenTemporary(path),
            Delay = async (retry, token) =>
            {
                retries.Add(retry);
                await share.RestoreAsync();
                // Keep the production backoff and attempt limit; the seam only controls the outage.
                await ordinary.Delay(retry, token);
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var progress = BackupProgressContext.Begin(job, Guid.NewGuid(), snapshot => stages.Add(snapshot.Progress.Stage));
        var storage = new FileBackupStorage(BackupDiskSpace.AvailableBytes, operations);
        var delivery = new BackupCompletion(storage, catalog)
            .CompleteAsync(job, source, Path.Combine(share.Root, "stage"), DateTimeOffset.UtcNow, timeout.Token);
        try
        {
            var first = await Task.WhenAny(paused.Task, delivery).WaitAsync(TimeSpan.FromSeconds(30), timeout.Token);
            if (first == delivery)
            {
                await delivery;
                throw new InvalidOperationException("Delivery completed before reaching the real SMB write barrier.");
            }
            var temporary = await paused.Task;
            // The writer has flushed a real first block to the server and cannot write the next
            // block until the controller has removed this share. No timing/large-file race.
            await share.DisconnectAsync(temporary);
            resume.TrySetResult();
            var completed = await delivery.WaitAsync(TimeSpan.FromSeconds(60), timeout.Token);
            var code = await actualError.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(code, new int?[] { 53, 54, 58, 59, 64, 121, 1222, 1231, 1232, 1236, 1237 });
            Assert.Equal(2, opens);
            Assert.Equal(new[] { 1 }, retries);
            Assert.Contains(BackupStage.RetryingStorage, stages);
            var records = await catalog.ReadAsync();
            Assert.Equal(2, records.Count);
            Assert.Single(records, record => record.Id == previous.Record.Id);
            Assert.Single(records, record => record.Id == completed.Record.Id);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(completed.Record.Backup.Location));
            Assert.Equal(previousBytes, await File.ReadAllBytesAsync(previous.Record.Backup.Location));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
            await new BackupCopyVerifier(storage).VerifyAsync(completed.Record, job);
            await new BackupCopyVerifier(storage).VerifyAsync(previous.Record, job);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)), completed.Record.Backup.Sha256);
            Assert.Empty(Directory.GetFiles(share.BackingDirectory, "*.tmp"));
        }
        finally
        {
            // Unblock the owned writer even when the controller/precondition failed.
            resume.TrySetResult();
            timeout.Cancel();
            try { await delivery.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { }
        }
    }

    private sealed class PausedSmbFile(string path, TaskCompletionSource<string> paused, Task resume, Action<IOException> failed)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous)
    {
        private bool first = true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            try
            {
                await base.WriteAsync(bytes, token);
                if (!first) return;
                first = false;
                await base.FlushAsync(token);
                base.Flush(flushToDisk: true);
                paused.TrySetResult(Name);
                await resume.WaitAsync(TimeSpan.FromSeconds(60), token);
            }
            catch (IOException error) { failed(error); throw; }
        }
        public override async Task FlushAsync(CancellationToken token)
        {
            try { await base.FlushAsync(token); }
            catch (IOException error) { failed(error); throw; }
        }
        public override void Flush(bool flushToDisk)
        {
            try { base.Flush(flushToDisk); }
            catch (IOException error) { failed(error); throw; }
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class SmbFixture : IAsyncDisposable
    {
        private readonly string id = Guid.NewGuid().ToString("N");
        private readonly string account = WindowsIdentity.GetCurrent().Name;
        private readonly string name;
        private readonly string marker;
        private readonly string? remoteRoot;
        private readonly string? controller;
        public string Root { get; }
        public string BackingDirectory => remoteRoot is null ? Path.Combine(Root, "share") : UncPath;
        public string UncPath => remoteRoot is null ? @"\\" + Environment.MachineName + @"\" + name : Path.Combine(remoteRoot, id);

        private SmbFixture()
        {
            name = "SPBackupCI_" + id;
            marker = "Owned SP Backup native SMB fixture " + id;
            Root = Path.Combine(Path.GetTempPath(), "SPBackup-native-smb-" + id);
            if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Refusing to reuse an existing SMB fixture directory.");
            var settingsPath = Environment.GetEnvironmentVariable("SPBACKUP_REMOTE_SMB_SETTINGS");
            if (settingsPath is not null)
            {
                if (Environment.GetEnvironmentVariable("SPBACKUP_REMOTE_SMB_TEST") != "1")
                    throw new InvalidOperationException("Separate-server SMB needs explicit disposable lab opt-in.");
                var settings = JsonSerializer.Deserialize<RemoteSmbSettings>(File.ReadAllText(settingsPath))
                    ?? throw new InvalidDataException("Missing separate-server SMB settings.");
                var parts = settings.UncRoot.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (!settings.UncRoot.StartsWith(@"\\", StringComparison.Ordinal) || parts.Length != 2
                    || parts[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    || parts[0] is "localhost" or "." or "127.0.0.1" or "::1"
                    || !Guid.TryParseExact(settings.Marker, "N", out _)
                    || File.ReadAllText(Path.Combine(settings.UncRoot, "fixture-owner.txt")) != settings.Marker
                    || !Path.IsPathFullyQualified(settings.ControllerDirectory)
                    || Directory.Exists(settings.ControllerDirectory))
                    throw new InvalidDataException("The separate SMB server/controller ownership preconditions failed.");
                remoteRoot = settings.UncRoot; controller = settings.ControllerDirectory;
                Directory.CreateDirectory(controller);
                Directory.CreateDirectory(Root);
            }
            Directory.CreateDirectory(BackingDirectory);
        }

        private sealed record RemoteSmbSettings(string UncRoot, string Marker, string ControllerDirectory);
        private async Task ExchangeAsync(string action, string? temporary = null)
        {
            var requestId = Guid.NewGuid();
            var request = Path.Combine(controller!, action + "-request.json");
            var response = Path.Combine(controller!, action + "-response.json");
            var temporaryRequest = request + ".tmp";
            await File.WriteAllTextAsync(temporaryRequest, JsonSerializer.Serialize(new { requestId,
                relativeDirectory = id, temporary = temporary is null ? null : Path.GetFileName(temporary) }));
            File.Move(temporaryRequest, request); // Publish the complete controller message.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (!File.Exists(response))
            {
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The separate SMB controller did not confirm " + action);
                await Task.Delay(100);
            }
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(response));
            if (json.RootElement.GetProperty("requestId").GetGuid() != requestId
                || !json.RootElement.GetProperty("succeeded").GetBoolean())
                throw new IOException("The separate SMB controller rejected " + action);
        }

        public static async Task<SmbFixture> CreateAsync()
        {
            if (!OperatingSystem.IsWindows()
                || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Native SMB verification requires an isolated administrator Windows runner.");
            var fixture = new SmbFixture();
            try
            {
                if (fixture.remoteRoot is not null)
                {
                    OutputRemoteFixture(fixture);
                    return fixture;
                }
                await fixture.RunAsync("""
                    if ((Get-Service -Name LanmanServer -ErrorAction Stop).Status -ne 'Running') {
                        throw 'LanmanServer must already be running; the fixture does not change services or network policy.'
                    }
                    foreach ($command in @('Get-SmbShare', 'New-SmbShare', 'Remove-SmbShare', 'Get-SmbOpenFile')) {
                        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required SMB command is unavailable: $command" }
                    }
                    if (Find-OwnedShare) { throw 'Refusing to reuse a pre-existing SMB share.' }
                    New-OwnedShare
                    """);
                // Confirm that UNC uses a real SMB server; no local-directory fallback exists.
                await File.WriteAllTextAsync(Path.Combine(fixture.UncPath, "access-check.txt"), "owned SMB fixture");
                File.Delete(Path.Combine(fixture.UncPath, "access-check.txt"));
                return fixture;
            }
            catch (Exception initializationError)
            {
                try { await fixture.DisposeAsync(); }
                catch (Exception cleanupError)
                { throw new AggregateException("Native SMB initialization and owned-resource cleanup failed.", initializationError, cleanupError); }
                throw;
            }
        }

        private static void OutputRemoteFixture(SmbFixture fixture) => Console.WriteLine(
            "Using separate SMB server: " + fixture.remoteRoot + "; controlled first-block interruption, not a local share.");

        public async Task DisconnectAsync(string temporary)
        {
            if (!string.Equals(Path.GetDirectoryName(temporary), UncPath, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(temporary).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to disconnect a file outside the owned SMB fixture.");
            if (remoteRoot is not null) { await ExchangeAsync("disconnect", temporary); return; }
            await RunAsync("""
                if (-not (Find-OwnedShare)) { throw 'The owned share disappeared before the fault barrier.' }
                $opened = @(Get-SmbOpenFile | Where-Object { $_.Path -ieq $settings.temporary })
                if ($opened.Count -ne 1 -or $opened[0].ClientUserName -ine $settings.account) {
                    throw 'The actual SMB handle does not belong to this fixture and runner account.'
                }
                if ((Get-Item -LiteralPath $settings.temporary -ErrorAction Stop).Length -le 0) {
                    throw 'No real first block was written to the SMB backing file.'
                }
                # Remove only the proven GUID-owned share; this closes the SMB tree with its
                # active file. Closing an arbitrary client handle is deliberately unnecessary.
                Remove-SmbShare -Name $settings.name -Force -Confirm:$false -ErrorAction Stop
                if (Find-OwnedShare) { throw 'The owned share remained available after forced removal.' }
                """, Path.Combine(BackingDirectory, Path.GetFileName(temporary)));
        }

        public Task RestoreAsync() => remoteRoot is not null ? ExchangeAsync("restore") : RunAsync("""
            if (Find-OwnedShare) { throw 'The share was unexpectedly restored before the production retry.' }
            New-OwnedShare
            """);

        private async Task RunAsync(string command, string? temporary = null)
        {
            var parameters = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { name, marker, account, backing = BackingDirectory, temporary })));
            // Data crosses the shell boundary as base64 JSON. Paths/accounts never become shell code.
            var script = "$ErrorActionPreference = 'Stop'\n$settings = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
                + parameters + "')) | ConvertFrom-Json\n" + """
                function Find-OwnedShare {
                    $share = Get-SmbShare -Name $settings.name -ErrorAction SilentlyContinue
                    if ($share -and ($share.Path -ine $settings.backing -or $share.Description -ne $settings.marker)) {
                        throw 'Refusing to modify a share whose path/ownership marker does not match this fixture.'
                    }
                    return $share
                }
                function New-OwnedShare {
                    New-SmbShare -Name $settings.name -Path $settings.backing -Description $settings.marker `
                        -FullAccess $settings.account -CachingMode None -ErrorAction Stop | Out-Null
                    if (-not (Find-OwnedShare)) { throw 'The owned SMB share was not created.' }
                }
                """ + "\n" + command;
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var process = Process.Start(start) ?? throw new IOException("The owned SMB controller did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0)
                    throw new IOException("The owned SMB controller failed: " + await stderr + "\n" + await stdout);
                await Task.WhenAll(stdout, stderr);
            }
            catch
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (remoteRoot is not null)
            {
                if (Directory.Exists(UncPath)) Directory.Delete(UncPath, recursive: true);
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return; // The separate controller owns the share; never invoke local SMB administration.
            }
            await RunAsync("""
                if (Find-OwnedShare) {
                    Remove-SmbShare -Name $settings.name -Force -Confirm:$false -ErrorAction Stop
                }
                """);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
