namespace SPBackup.Core;

/// <summary>Only exact commands for this installation belong to its startup setting.</summary>
public sealed class AgentAutostartCommands(string installation)
{
    public string Current => Command("SPBackup.Desktop.exe", "--tray --start-agent");
    private string Command(string executable, string arguments) => $"\"{Path.Combine(installation, executable)}\" {arguments}";
    public bool Owns(string? value) => value is not null && new[] {
        Current, Command("OneCBackup.Desktop.exe", "--tray --start-agent"),
        Command("OneCBackup.Agent.exe", "--run --quiet"), Command("SPBackup.Agent.exe", "--run --quiet")
    }.Any(command => string.Equals(command, value, StringComparison.OrdinalIgnoreCase));
    public bool CanEnable(string? existing) => existing is null || Owns(existing);
}
