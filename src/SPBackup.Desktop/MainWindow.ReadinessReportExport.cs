using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    internal Func<SaveFileDialog, bool?>? ReadinessReportExportDialogOverride { get; set; }

    private string ReadinessReportText() => ApplicationBuildInfo.SupportText + "\n\n"
        + StorageCheckResults.Header + "\n" + ReadinessReportState.Text + "\n\n" + StorageCheckDetails.Text
        + "\n\nОтчёт содержит пути и имя пользователя исполнителя. Перед отправкой проверьте его содержимое.\n";

    private void CopyReadinessReport_Click(object sender, RoutedEventArgs e)
    {
        if (!CopyReadinessReportButton.IsEnabled) return;
        try
        {
            Clipboard.SetText(ReadinessReportText());
            ReadinessReportActionStatus.Text = "Отчёт скопирован. Перед отправкой проверьте пути и имя пользователя в тексте.";
        }
        catch (ExternalException)
        { ReadinessReportActionStatus.Text = "Буфер обмена занят. Повторите копирование или выделите текст отчёта вручную."; }
    }

    private async void SaveReadinessReport_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveReadinessReportButton.IsEnabled) return;
        var text = ReadinessReportText();
        var dialog = new SaveFileDialog { Title = "Сохранить отчёт проверки", FileName = "SP-Backup-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt",
            Filter = "Текстовый отчёт (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true,
            OverwritePrompt = false, CheckPathExists = true };
        if ((ReadinessReportExportDialogOverride is null ? dialog.ShowDialog(this) : ReadinessReportExportDialogOverride(dialog)) != true) return;
        jobFileTransfer = true; UpdateRunControls();
        try
        {
            var target = Path.GetFullPath(dialog.FileName);
            var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DesktopPaths.DataDirectory)) + Path.DirectorySeparatorChar;
            if (target.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Выберите папку вне рабочего профиля приложения.");
            await ReadinessReportFile.SaveNewAsync(target, text);
            ReadinessReportActionStatus.Text = "Отчёт сохранён: " + target + ". Перед отправкой проверьте его содержимое.";
        }
        catch (IOException ex) when (File.Exists(dialog.FileName))
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Warning, ex); ReadinessReportActionStatus.Text = "Файл уже существует. Выберите новое имя; существующие файлы не заменяются."; }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex); ReadinessReportActionStatus.Text = "Не удалось сохранить отчёт: " + ex.Message; }
        finally { jobFileTransfer = false; UpdateRunControls(); }
    }

    private void ClearReadinessReport_Click(object sender, RoutedEventArgs e)
    {
        if (!ClearReadinessReportButton.IsEnabled) return;
        readinessKind = null; readinessOutcome = null;
        storageCheckJobId = null; readinessJobFingerprint = null; readinessReportStale = false;
        StorageCheckDetails.Clear(); StorageCheckResults.IsExpanded = false;
        ReadinessReportState.Text = ReadinessReportActionStatus.Text = "";
        Status.Text = "Отчёт проверки очищен. Задания, история и резервные копии сохранены.";
        UpdateRunControls();
    }
}
