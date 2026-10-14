namespace OneCBackup.Core;

internal sealed class AgentRuntimeWarnings
{
    private readonly Dictionary<string, string> warnings = [];
    private readonly Dictionary<string, (Type, int)> fingerprints = [];
    private readonly object gate = new();
    internal static bool Recoverable(Exception error) => error is not InvalidDataException
        && error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    internal void Set(string category, Exception? error)
    {
        lock (gate)
        {
            if (error is null) { warnings.Remove(category); fingerprints.Remove(category); return; }
            var text = category + ": " + error.Message;
            var fingerprint = (error.GetType(), error.HResult);
            if (!fingerprints.TryGetValue(category, out var previous) || previous != fingerprint)
                DiagnosticLog.Current.Write(DiagnosticEvent.AgentWarning, DiagnosticLevel.Warning, error);
            fingerprints[category] = fingerprint;
            warnings[category] = text;
        }
    }
    internal string? Summary
    { get { lock (gate) return warnings.Count == 0 ? null : string.Join("\n", warnings.Values); } }
}
