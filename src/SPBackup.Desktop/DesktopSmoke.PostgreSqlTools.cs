using System.IO;
using System.Windows;
using System.Windows.Controls;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyPostgreSqlToolsAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name)
        {
            var button = Control<Button>(name); button.BringIntoView(); window.UpdateLayout();
            if (!button.IsVisible || !button.IsEnabled) throw new InvalidOperationException("Действие PostgreSQL недоступно: " + name);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        }
        var root = Path.Combine(Path.GetDirectoryName(DesktopPaths.DataDirectory)!, "postgres-client-tools");
        foreach (var version in new[] { "17", "16" })
        {
            var bin = Path.Combine(root, version, "bin"); Directory.CreateDirectory(bin);
            foreach (var name in new[] { "pg_dump.exe", "pg_restore.exe", "psql.exe" }) File.WriteAllText(Path.Combine(bin, name), "fixture; never execute");
        }
        try
        {
            Click("JobsNavigation"); Click("NewJobButton");
            Control<ComboBox>("Kind").SelectedItem = DatabaseKind.PostgreSql;
            Control<ComboBox>("PgAuthenticationPicker").SelectedIndex = 1; // This scenario only checks discovery; saved-password entry is tested separately.
            Control<TextBox>("JobName").Text = "PostgreSQL — поиск утилит"; Control<TextBox>("Source").Text = "fixture_db";
            Click("NextStepButton");
            if (!Control<TextBlock>("WizardError").Text.Contains("Найти PostgreSQL"))
                throw new InvalidOperationException("Мастер не объясняет, как найти утилиты PostgreSQL.");
            window.PostgreSqlToolsDiscoveryOverride = () => PostgreSqlToolDiscovery.Find([Path.Combine(root, "16"), Path.Combine(root, "17")]);
            Click("FindPostgreSqlToolsButton");
            await WaitUntil(() => Control<Button>("FindPostgreSqlToolsButton").IsEnabled, "Поиск утилит не завершился.");
            var picker = Control<ComboBox>("PostgreSqlToolsPicker");
            if (!picker.IsVisible || picker.Items.Count != 2 || !Control<TextBox>("PgDumpPath").Text.Contains(Path.Combine("17", "bin")))
                throw new InvalidOperationException("Поиск PostgreSQL не выбрал полную пару новой версии.");
            picker.SelectedIndex = 1;
            if (Control<TextBox>("PgDumpPath").Text != Path.Combine(root, "16", "bin", "pg_dump.exe")
                || Control<TextBox>("PgRestorePath").Text != Path.Combine(root, "16", "bin", "pg_restore.exe"))
                throw new InvalidOperationException("Выбор версии PostgreSQL смешал пути разных установок.");
            Control<TextBlock>("PostgreSqlToolsStatus").BringIntoView(); screenshot("postgres-tools-found");
            window.PostgreSqlToolsDiscoveryOverride = () => [];
            Click("FindPostgreSqlToolsButton");
            await WaitUntil(() => Control<Button>("FindPostgreSqlToolsButton").IsEnabled, "Поиск без утилит не завершился.");
            if (!Control<TextBlock>("PostgreSqlToolsStatus").Text.Contains("не найдены")
                || Control<TextBox>("PgDumpPath").Text != Path.Combine(root, "16", "bin", "pg_dump.exe"))
                throw new InvalidOperationException("Неудачный поиск затёр ручные пути PostgreSQL.");
            Click("NextStepButton");
            if (!Control<StackPanel>("StorageStep").IsVisible)
                throw new InvalidOperationException("Найденные пути PostgreSQL не прошли мастер.");
            Click("CancelEditorButton");
        }
        finally { window.PostgreSqlToolsDiscoveryOverride = null; Directory.Delete(root, true); }
    }
}
