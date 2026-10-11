using System.Diagnostics;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class NativeSqlFactAttribute : FactAttribute
{
    public NativeSqlFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ONECBACKUP_NATIVE_SQL") != "1")
            Skip = "Opt-in Windows integration with fresh SQL Server LocalDB and PostgreSQL fixtures.";
    }
}

internal static class NativeSqlTestTools
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment)
                if (pair.Value is null) start.Environment.Remove(pair.Key); else start.Environment[pair.Key] = pair.Value;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start) ?? throw new IOException("Could not start fixture tool: " + Path.GetFileName(executable));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            var error = await errors;
            if (process.ExitCode != 0) throw new IOException(Path.GetFileName(executable) + " failed: " + error + "\n" + text);
            return text.Trim();
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            throw;
        }
    }
}
