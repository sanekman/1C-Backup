using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyJobTransferAsync(MainWindow window, Action<string> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Click("JobsNavigation");
        var grid = Control<DataGrid>("Jobs");
        var original = grid.Items.OfType<BackupJob>().First(job => job.ArchivePasswordKey is not null);
        grid.SelectedItem = original;
        var store = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
        var catalog = new BackupCatalog(Path.Combine(DesktopPaths.DataDirectory, "copies.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        var originalJobs = JsonSerializer.Serialize(await store.LoadAsync());
        var beforeCopies = JsonSerializer.Serialize(await catalog.ReadAsync());
        var beforeRuns = JsonSerializer.Serialize(await history.ReadAsync());
        var directory = Path.GetDirectoryName(DesktopPaths.DataDirectory)!;
        var path = Path.Combine(directory, "job-settings.json");
        async Task Export(string target)
        {
            window.JobExportDialogOverride = dialog => { dialog.FileName = target; return true; };
            Control<MenuItem>("ExportJobSettingsMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Настройки задания не экспортировались.");
        }
        async Task Import(string source)
        {
            window.JobImportDialogOverride = dialog => { dialog.FileName = source; return true; };
            Control<MenuItem>("ImportJobSettingsMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => window.SmokeReady, "Настройки задания не загрузились.");
            if (!window.HasUnsavedJob)
                throw new InvalidOperationException("Импортированное задание не защищено до первого изменения поля.");
        }
        try
        {
            await Export(path);
            if (!File.Exists(path) || !Control<TextBlock>("Status").Text.Contains("Настройки сохранены"))
                throw new InvalidOperationException("Настройки задания не сохранены в выбранный файл.");
            var text = await File.ReadAllTextAsync(path);
            var secret = await new WindowsArchivePasswordStore().GetAsync(original.ArchivePasswordKey!);
            if (text.Contains(secret, StringComparison.Ordinal) || text.Contains(JsonSerializer.Serialize(secret), StringComparison.Ordinal))
                throw new InvalidOperationException("Значение пароля попало в экспорт настроек.");
            await Export(path);
            if (!Control<TextBlock>("Status").Text.Contains("уже существует") || await File.ReadAllTextAsync(path) != text)
                throw new InvalidOperationException("Экспорт настроек перезаписал существующий файл.");
            await Export(Path.Combine(DesktopPaths.DataDirectory, "unsafe-template.json"));
            if (!Control<TextBlock>("Status").Text.Contains("вне рабочего профиля") || File.Exists(Path.Combine(DesktopPaths.DataDirectory, "unsafe-template.json")))
                throw new InvalidOperationException("Экспорт настроек разрешён в рабочий профиль.");
            await Import(path);
            if (!Control<Border>("EditorCard").IsVisible || Control<CheckBox>("Enabled").IsChecked != false
                || !Control<TextBlock>("WizardHint").Text.Contains("ещё не сохранены"))
                throw new InvalidOperationException("Импорт не открыл отключённый черновик в мастере.");
            screenshot("job-settings-import-draft");
            Click("CancelEditorButton");
            if (JsonSerializer.Serialize(await store.LoadAsync()) != originalJobs)
                throw new InvalidOperationException("Отмена импорта изменила сохранённые задания.");
            await Import(path);
            Click("NextStepButton"); Click("NextStepButton"); Click("NextStepButton");
            screenshot("job-settings-import-review");
            Click("SaveButton");
            await WaitUntil(() => window.SmokeReady && Control<Border>("JobListCard").IsVisible, "Импортированное задание не сохранилось.");
            var savedJobs = await store.LoadAsync();
            var imported = savedJobs.Single(job => job.Name == original.Name + " — импорт");
            if (imported.Id == original.Id || imported.Enabled
                || JsonSerializer.Serialize(savedJobs.Single(job => job.Id == original.Id)) != JsonSerializer.Serialize(original)
                || JsonSerializer.Serialize(imported) != JsonSerializer.Serialize(original with { Id = imported.Id, Name = imported.Name, Enabled = false }))
                throw new InvalidOperationException("Импорт изменил оригинал или потерял параметры / отключение нового задания.");
            var afterImport = JsonSerializer.Serialize(savedJobs);
            grid.SelectedItem = grid.Items.OfType<BackupJob>().Single(job => job.Id == original.Id);
            var invalid = JsonNode.Parse(text)!.AsObject(); invalid["FormatVersion"] = 99;
            var invalidPath = Path.Combine(directory, "unsupported-settings.json"); await File.WriteAllTextAsync(invalidPath, invalid.ToJsonString());
            await Import(invalidPath);
            if (Control<Border>("EditorCard").IsVisible || !Control<TextBlock>("Status").Text.Contains("Неизвестный формат"))
                throw new InvalidOperationException("Неподдерживаемый импорт открыл мастер или изменил профиль.");
            var missing = JsonNode.Parse(text)!.AsObject(); missing["Job"]!["ArchivePasswordKey"] = Guid.NewGuid().ToString("N");
            var missingPath = Path.Combine(directory, "missing-password-settings.json"); await File.WriteAllTextAsync(missingPath, missing.ToJsonString());
            await Import(missingPath);
            if (Control<CheckBox>("EncryptArchive").IsChecked != true || !Control<TextBlock>("WizardHint").Text.Contains("Пароль архива недоступен"))
                throw new InvalidOperationException("Импорт с недоступным паролем молча выключил шифрование.");
            Click("NextStepButton"); Click("NextStepButton");
            if (!Control<StackPanel>("StorageStep").IsVisible || Control<TextBlock>("WizardError").Visibility != Visibility.Visible
                || !Control<TextBlock>("WizardError").Text.Contains("пароль"))
                throw new InvalidOperationException("Недоступный пароль не требует настройки на шаге хранилищ.");
            screenshot("job-settings-import-password-required");
            Click("CancelEditorButton");
            if (JsonSerializer.Serialize(await store.LoadAsync()) != afterImport || JsonSerializer.Serialize(await catalog.ReadAsync()) != beforeCopies
                || JsonSerializer.Serialize(await history.ReadAsync()) != beforeRuns)
                throw new InvalidOperationException("Перенос настроек создал копии, запуски или изменил сохранённый профиль после ошибки.");
            screenshot("job-settings-import-saved-compact");
        }
        finally { window.JobExportDialogOverride = null; window.JobImportDialogOverride = null; }
    }
}
