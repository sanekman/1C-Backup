using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static class ApplicationUpdateHandoff
{
    public static bool IsInstalled()
    {
        foreach (var registration in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var hive = RegistryKey.OpenBaseKey(registration, RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{142FA512-738F-4BE8-9C31-487B3500B30B}_is1");
            if (key?.GetValue("InstallLocation") is not string location || !Path.IsPathFullyQualified(location)) continue;
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private static string ReadScript()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("OneCBackup.Desktop.UpdateHandoff.ps1")
            ?? throw new IOException("Не найден компонент установки обновлений.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    internal static async Task ValidateScriptSyntaxAsync()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(ReadScript()));
        var command = "$text=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded
            + "'));$tokens=$null;$errors=$null;[System.Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)|Out-Null;if($errors.Count -ne 0){$errors|ForEach-Object{$_.ToString()};exit 1}";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var process = Process.Start(start) ?? throw new IOException("Не найден Windows PowerShell.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        if (process.ExitCode != 0) throw new InvalidDataException("Ошибка синтаксиса компонента обновления: " + await output + await error);
    }

    public static async Task StartAsync(ApplicationUpdate update, string installer, bool restart, bool restartAgent, bool restartWindowSchedule = false)
    {
        if (!IsInstalled()) throw new InvalidOperationException("Автообновление доступно для установленной программы. Переносную версию обновите из ZIP.");
        if (!await GitHubApplicationUpdates.IsVerifiedAsync(installer, update.Manifest))
            throw new InvalidDataException("Скачанное обновление изменилось. Повторите скачивание.");
        var directory = Path.Combine(DesktopPaths.DataDirectory, "updates");
        Directory.CreateDirectory(directory);
        var planPath = Path.Combine(directory, "install-plan-" + Guid.NewGuid().ToString("N") + ".json");
        using var process = Process.GetCurrentProcess();
        var plan = new { ParentId = process.Id, ParentStartedTicks = process.StartTime.ToUniversalTime().Ticks,
            Installer = Path.GetFullPath(installer), update.Manifest.Size, update.Manifest.Sha256, update.Manifest.Version, update.Manifest.InstallerKind,
            InstallDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Restart = restart, RestartAgent = restartAgent, RestartWindowSchedule = restartWindowSchedule };
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), Encoding.UTF8);
        var script = "$planPath = '" + planPath.Replace("'", "''") + "'\n" + ReadScript();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        DiagnosticLog.Current.Write(DiagnosticEvent.UpdateInstallationStarted);
        using var helper = Process.Start(start) ?? throw new IOException("Не удалось запустить установку обновления.");
        await Task.Delay(500);
        if (helper.HasExited) throw new IOException("Компонент обновления завершился до закрытия программы. Проверьте updates/update-result.json.");
    }
}
