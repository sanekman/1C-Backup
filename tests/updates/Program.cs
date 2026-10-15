using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OneCBackup.Core;

var previousVersion = args.Length > 0 ? args[0] : "0.0.0";
var expectedVersion = args.Length > 1 ? args[1] : null;
var directory = Path.GetFullPath(args.Length > 2 ? args[2] : "artifacts/update-download");
Directory.CreateDirectory(directory);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
using var client = GitHubApplicationUpdates.CreateClient();
var updates = new GitHubApplicationUpdates(client);
var secret = Environment.GetEnvironmentVariable("GH_TOKEN");
var update = await updates.CheckAsync(previousVersion, true, secret, timeout.Token)
    ?? throw new InvalidOperationException("No published update was found.");
if (expectedVersion is not null && update.Manifest.Version != expectedVersion)
    throw new InvalidOperationException("The selected release differs from the expected version.");
var progress = new List<int>();
var retries = new List<UpdateRetryNotice>();
var path = await updates.DownloadAsync(update, directory, secret, new ImmediateProgress<int>(progress.Add),
    timeout.Token, new ImmediateProgress<UpdateRetryNotice>(retries.Add));
if (!await GitHubApplicationUpdates.IsVerifiedAsync(path, update.Manifest, timeout.Token)
    || progress.Count == 0 || progress[^1] != 100 || Directory.GetFiles(directory, "*.tmp").Length != 0)
    throw new InvalidDataException("The downloaded installer or completion state is invalid.");
var lastWrite = File.GetLastWriteTimeUtc(path);
if (await updates.DownloadAsync(update, directory, secret, token: timeout.Token) != path
    || File.GetLastWriteTimeUtc(path) != lastWrite)
    throw new InvalidOperationException("The verified cached installer was not reused.");
if (await updates.CheckAsync(update.Manifest.Version, true, secret, timeout.Token) is not null)
    throw new InvalidOperationException("The current version is offered an update again.");
using var installer = File.OpenRead(path);
var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(installer, timeout.Token)).ToLowerInvariant();
var report = new {
    Success = true, PreviousVersion = previousVersion, Version = update.Manifest.Version,
    update.DistributionBranch, Bytes = installer.Length, Sha256 = sha256,
    CompleteProgress = progress[^1], VerifiedCacheReused = true, CurrentVersionNotOfferedAgain = true,
    PartialFilesRemaining = 0, Retries = retries, Runtime = Environment.Version.ToString(),
    SourceRevision = typeof(GitHubApplicationUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), json, timeout.Token);
Console.WriteLine(json);

sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
