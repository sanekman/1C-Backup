using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    internal Func<SaveFileDialog, bool?>? DiagnosticExportDialogOverride { get; set; }
    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (!ExportDiagnosticsButton.IsEnabled) return;
        var dialog = new SaveFileDialog { Title = "Сохранить диагностику для анализа", FileName = "1C-Backup-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
            DefaultExt = ".zip", Filter = "Диагностика ZIP (*.zip)|*.zip", AddExtension = true, OverwritePrompt = false, CheckPathExists = true };
        if ((DiagnosticExportDialogOverride is null ? dialog.ShowDialog(this) : DiagnosticExportDialogOverride(dialog)) != true) return;
        jobFileTransfer = true; UpdateRunControls();
        DiagnosticsStatus.Text = Status.Text = "Собираются логи и сведения о программе. Копии и настройки не изменяются.";
        try
        {
            var result = await Task.Run(() => new DiagnosticBundle(DesktopPaths.DataDirectory, ApplicationBuildInfo.Version, ApplicationBuildInfo.Revision).ExportAsync(dialog.FileName));
            DiagnosticsStatus.Text = $"Диагностика сохранена: {result.Path}\nСобытий: {result.Events}; файлов логов: {result.LogFiles}; запусков: {result.Runs}. Отправьте этот ZIP для анализа."
                + (result.Skipped > 0 ? $" Пропущено недоступных или неполных записей: {result.Skipped}." : "")
                + (result.LogFiles == 0 ? " Логов нет: сведения о состоянии записи включены в ZIP." : "");
            Status.Text = "Архив диагностики готов. Отправьте ZIP вместе с примерным временем проблемы.";
        }
        catch (Exception ex)
        {
            DiagnosticLog.Current.Write(DiagnosticEvent.DiagnosticExportFailed, DiagnosticLevel.Error, ex);
            DiagnosticsStatus.Text = Status.Text = "Не удалось сохранить диагностику: " + ex.Message;
        }
        finally { jobFileTransfer = false; UpdateRunControls(); }
    }
    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(DesktopPaths.DataDirectory, "logs");
            if (!Directory.Exists(path)) { DiagnosticsStatus.Text = "Папка логов ещё не создана. Сохраните диагностику для проверки состояния записи."; return; }
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = '"' + path + '"', UseShellExecute = true });
        }
        catch (Exception ex) { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); DiagnosticsStatus.Text = "Не удалось открыть логи: " + ex.Message; }
    }
}
