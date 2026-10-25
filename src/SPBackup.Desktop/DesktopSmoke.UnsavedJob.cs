using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SPBackup.Core;

namespace SPBackup.Desktop;

internal static partial class DesktopSmoke
{
    private static async Task VerifyUnsavedJobAsync(MainWindow window, Action<string, Window> screenshot)
    {
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        var previous = window.UnsavedJobDialogOpened;
        var prompts = 0;
        var choice = "ReturnToDraftButton";
        var captureDialog = false;
        window.UnsavedJobDialogOpened = dialog =>
        {
            prompts++;
            dialog.Loaded += (_, _) =>
            {
                dialog.UpdateLayout();
                if (captureDialog) screenshot("unsaved-job", dialog);
                if (choice == "Close") { dialog.Close(); return; }
                ((Button)dialog.FindName(choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // A directly raised Click does not invoke WPF's IsCancel command.
                if (choice == "ReturnToDraftButton" && dialog.IsVisible) dialog.Close();
            };
        };
        var store = new JobStore(Path.Combine(DesktopPaths.DataDirectory, "jobs.json"));
        var history = new BackupRunHistory(Path.Combine(DesktopPaths.DataDirectory, "runs.json"));
        var beforeRuns = JsonSerializer.Serialize(await history.ReadAsync());
        try
        {
            Click("JobsNavigation");
            Control<TextBox>("JobSearch").Clear();
            Click("NewJobButton");
            Require(!window.HasUnsavedJob, "Пустое новое задание ошибочно считается изменённым.");
            Click("CancelEditorButton");
            Require(prompts == 0, "Пустой мастер не должен требовать подтверждения.");

            Click("NewJobButton");
            Control<TextBox>("JobName").Text = "Проверка несохранённого задания";
            Control<ComboBox>("Kind").SelectedItem = DatabaseKind.FilesAndFolders;
            Require(window.HasUnsavedJob, "Введённое название не защищено.");
            captureDialog = true;
            Click("CancelEditorButton");
            captureDialog = false;
            Require(prompts == 1 && window.HasUnsavedJob && Control<Border>("EditorCard").IsVisible,
                "Возврат из подтверждения потерял черновик.");
            Require(!Control<Button>("SaveAgentSettingsButton").IsEnabled,
                "Смена режима агента может перезаписать открытый черновик.");

            // Saving an incomplete job must leave the editor open at the error.
            choice = "SaveDraftButton";
            Click("CancelEditorButton");
            Require(Control<TextBlock>("WizardError").IsVisible && window.HasUnsavedJob,
                "Ошибка проверки не сохранила открытый черновик.");
            var root = Path.Combine(Path.GetDirectoryName(DesktopPaths.DataDirectory)!, "unsaved-job");
            Directory.CreateDirectory(Path.Combine(root, "source"));
            Control<TextBox>("Source").Text = Path.Combine(root, "source");
            Control<TextBox>("Destination").Text = Path.Combine(root, "copies");
            Control<ComboBox>("Schedule").SelectedValue = ScheduleKind.Manual;
            Click("CancelEditorButton");
            await WaitUntil(() => !window.HasUnsavedJob && window.SmokeReady, "Задание не сохранено из подтверждения.");
            Require(!Control<Border>("EditorCard").IsVisible, "После сохранения мастер не закрылся.");
            var saved = (await store.LoadAsync()).Single(job => job.Name == "Проверка несохранённого задания");
            Require(saved.Kind == DatabaseKind.FilesAndFolders && saved.ScheduleKind == ScheduleKind.Manual,
                "Сохранение из первого шага потеряло настройки.");
            var beforeJobs = JsonSerializer.Serialize(await store.LoadAsync());

            // A failed save during a full exit must not stop the agent or lose the draft.
            Click("EditJobButton");
            Control<TextBox>("JobName").Text += " — ошибка записи";
            var jobsPath = Path.Combine(DesktopPaths.DataDirectory, "jobs.json");
            var preservedPath = jobsPath + ".unsaved-smoke";
            File.Move(jobsPath, preservedPath);
            Directory.CreateDirectory(jobsPath);
            try
            {
                Click("ExitFullyButton");
                await WaitUntil(() => window.SmokeReady, "Ошибка сохранения не вернула управление.");
                Require(window.HasUnsavedJob && window.IsEnabled && window.IsVisible
                    && Control<TextBlock>("Status").Text.Contains("Не удалось сохранить"),
                    "Ошибка сохранения при выходе потеряла черновик или закрыла окно.");
            }
            finally
            {
                Directory.Delete(jobsPath);
                File.Move(preservedPath, jobsPath);
            }
            choice = "DiscardDraftButton";
            Click("CancelEditorButton");
            Click("EditJobButton");
            var beforePrompt = prompts;
            Require(!window.HasUnsavedJob, "Загрузка сохранённого задания помечена изменением.");
            Click("NextStepButton"); Click("NextStepButton"); Click("NextStepButton");
            Require(!window.HasUnsavedJob, "Переход по шагам создал ложные изменения.");
            Control<TextBox>("JobName").Text += " — исправление";
            Control<TextBox>("JobName").Text = saved.Name;
            Require(!window.HasUnsavedJob, "Возврат исходного значения не снял признак изменения.");
            Click("CancelEditorButton");
            Require(prompts == beforePrompt, "Неизменённое задание вызвало подтверждение.");

            Click("EditJobButton");
            Control<TextBox>("Source").Text += "-draft";
            choice = "ReturnToDraftButton";
            beforePrompt = prompts;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Require(prompts == beforePrompt + 1 && window.HasUnsavedJob, "Esc не сохранил черновик после возврата.");
            choice = "Close";
            Click("CancelEditorButton");
            Require(window.HasUnsavedJob && Control<Border>("EditorCard").IsVisible,
                "Закрытие подтверждения крестиком потеряло черновик.");
            choice = "ReturnToDraftButton";

            // Hiding the window must preserve the draft and avoid any prompt.
            window.SmokeMode = false;
            beforePrompt = prompts;
            window.Close();
            Require(!window.IsVisible && prompts == beforePrompt && window.HasUnsavedJob,
                "Сворачивание в трей изменило или потеряло черновик.");
            window.ShowFromTray(); window.SmokeMode = true;
            Click("ExitFullyButton");
            Require(prompts == beforePrompt + 1 && window.IsVisible && window.IsEnabled && window.HasUnsavedJob,
                "Отмена полного выхода не оставила редактор доступным.");
            choice = "DiscardDraftButton";
            Click("CancelEditorButton");
            Require(!window.HasUnsavedJob && JsonSerializer.Serialize(await store.LoadAsync()) == beforeJobs,
                "Отказ от сохранения изменил задание на диске.");

            Click("DuplicateJobButton");
            Require(window.HasUnsavedJob, "Новая копия задания не защищена до первого изменения поля.");
            beforePrompt = prompts;
            Click("CancelEditorButton");
            Require(prompts == beforePrompt + 1 && !window.HasUnsavedJob, "Дублированное задание потеряно без подтверждения.");
            Require(JsonSerializer.Serialize(await history.ReadAsync()) == beforeRuns,
                "Сохранение черновика неожиданно запустило копирование.");
        }
        finally { window.UnsavedJobDialogOpened = previous; window.SmokeMode = true; }
    }
}
