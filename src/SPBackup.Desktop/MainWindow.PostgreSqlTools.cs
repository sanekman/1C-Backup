using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class MainWindow
{
    private int postgresToolSearchGeneration;
    internal Func<IReadOnlyList<PostgreSqlToolInstallation>>? PostgreSqlToolsDiscoveryOverride { get; set; }

    private void ResetPostgreSqlTools()
    {
        ++postgresToolSearchGeneration;
        FindPostgreSqlToolsButton.IsEnabled = true;
        PostgreSqlToolsPicker.ItemsSource = null;
        PostgreSqlToolsPicker.Visibility = Visibility.Collapsed;
        PostgreSqlManualTools.IsExpanded = false;
        PostgreSqlToolsStatus.Text = "Можно найти установленные утилиты или выбрать файлы вручную ниже.";
    }

    private async void FindPostgreSqlTools_Click(object sender, RoutedEventArgs e)
    {
        var generation = ++postgresToolSearchGeneration;
        var dump = PgDumpPath.Text; var restore = PgRestorePath.Text;
        FindPostgreSqlToolsButton.IsEnabled = false;
        PostgreSqlToolsStatus.Text = "Ищем установленный PostgreSQL на этом компьютере…";
        try
        {
            Func<IReadOnlyList<PostgreSqlToolInstallation>> discover = PostgreSqlToolsDiscoveryOverride ?? PostgreSqlToolDiscovery.FindInstalledOnWindows;
            var installations = await Task.Run(discover);
            if (generation != postgresToolSearchGeneration || !editorOpen || Kind.SelectedItem is not DatabaseKind.PostgreSql) return;
            if (wizardStep != 0)
            {
                PostgreSqlToolsStatus.Text = "Поиск завершился после перехода на другой шаг. Пути не изменены; при необходимости повторите поиск на шаге источника.";
                return;
            }
            if (dump != PgDumpPath.Text || restore != PgRestorePath.Text)
            {
                PostgreSqlToolsStatus.Text = "Пути изменены во время поиска. Ваши значения сохранены; повторите поиск при необходимости.";
                return;
            }
            PostgreSqlToolsPicker.ItemsSource = installations;
            PostgreSqlToolsPicker.Visibility = installations.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (installations.Count == 0)
            {
                PostgreSqlToolsStatus.Text = "Утилиты не найдены. Установите клиентские утилиты PostgreSQL на этот компьютер или выберите pg_dump.exe и pg_restore.exe вручную. Введённые пути не изменены.";
                return;
            }
            PostgreSqlToolsPicker.SelectedIndex = 0;
        }
        catch (Exception error)
        {
            if (generation == postgresToolSearchGeneration)
                PostgreSqlToolsStatus.Text = "Не удалось найти утилиты: " + error.Message + " Выберите файлы вручную ниже.";
        }
        finally { if (generation == postgresToolSearchGeneration) FindPostgreSqlToolsButton.IsEnabled = true; }
    }

    private void PostgreSqlTools_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PostgreSqlToolsPicker.SelectedItem is not PostgreSqlToolInstallation installation) return;
        PgDumpPath.Text = installation.PgDumpPath; PgRestorePath.Text = installation.PgRestorePath;
        PostgreSqlToolsStatus.Text = "Выбраны утилиты: " + installation.Directory
            + ". Их версия должна быть не старше сервера базы."
            + (installation.HasPsql ? "" : " Для кнопки «Проверить источник» нужен также psql.exe в этой папке.");
    }
}
