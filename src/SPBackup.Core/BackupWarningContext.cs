namespace SPBackup.Core;

/// <summary>Execution observations belong to the current run, not to the saved job options.</summary>
internal static class BackupWarningContext
{
    private static readonly AsyncLocal<Action<string>?> current = new();
    internal static IDisposable Begin(Action<string> report)
    {
        var previous = current.Value;
        current.Value = report;
        return new Scope(() => current.Value = previous);
    }
    internal static void Report(string warning) => current.Value?.Invoke(warning);
    private sealed class Scope(Action end) : IDisposable { public void Dispose() => end(); }
}
