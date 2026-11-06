using System.Windows;

namespace SPBackup.Desktop;

internal static class SqlCertificateWarning
{
    internal static bool Confirm(Window owner) => MessageBox.Show(owner,
        "Соединение останется зашифрованным, но SP Backup не сможет проверить подлинность MS SQL. "
        + "Это позволяет подключаться к серверу с самоподписанным или недоверенным сертификатом.\n\n"
        + "При подмене сервера злоумышленник может получить данные и учётные сведения. "
        + "Включайте эту настройку только для известного сервера в доверенной сети.\n\n"
        + "Разрешить подключение без проверки сертификата для этого задания?",
        "MS SQL: отключение проверки сертификата", MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
