using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private async Task<string> RememberChecksAsync(IEnumerable<BackupVerificationEntry> entries)
    {
        try
        {
            await verificationState.RememberAsync(entries, await EffectiveCatalog.ReadAsync());
            await RefreshCopiesAsync();
            return "";
        }
        catch (Exception ex)
        { DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Error, ex);
            await RefreshCopiesAsync();
            return " Результат проверки не сохранён: " + ex.Message;
        }
    }
}
