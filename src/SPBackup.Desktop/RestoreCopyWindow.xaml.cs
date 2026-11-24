using System.Windows;
using Microsoft.Win32;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class RestoreCopyWindow : Window
{
    private CancellationTokenSource? readingContents;
    private readonly bool mixedChain;
    internal RestoreCopyWindow(BackupCopyRow copy, bool files, int archives, int storages = 0, bool showFileSelection = true,
        bool canReadOtherDestinations = false, bool requireOtherDestinations = false)
    {
        InitializeComponent();
        CopyName.Text = $"{copy.JobName} · {copy.CompletedUtc.ToLocalTime():dd.MM.yyyy HH:mm}";
        CopySource.Text = copy.DisplayLocation;
        ChainStatus.Text = files
            ? (requireOtherDestinations ? $"Для восстановления нужно прочитать архивы из других хранилищ задания. Архивов: {archives}, хранилищ: {storages}."
                : $"В каталоге найдены все архивы цепочки ({archives}). При восстановлении программа проверит каждый архив.")
                + (copy.Record.Backup.FileSnapshot?.IsIncomplete == true ? "\nКопия с пропусками. Могут восстановиться прежние версии файлов." : "")
            : "Из ZIP будет извлечён файл 1Cv8.1CD. Перед извлечением программа проверит архив.";
        FolderHint.Text = files ? "В выбранной папке будет создан новый подкаталог Restored-files-…"
            : "В выбранной папке будет создан новый подкаталог 1C-Restored-…";
        var encrypted = copy.Record.Backup.ArchivePasswordKey is not null;
        PasswordHint.Text = encrypted
            ? "Пароль архива из Windows будет использован автоматически."
            : "Пароль архива не требуется по данным каталога.";
        AlternatePassword.Visibility = encrypted ? Visibility.Visible : Visibility.Collapsed;
        FileSelection.Visibility = files && showFileSelection ? Visibility.Visible : Visibility.Collapsed;
        mixedChain = requireOtherDestinations;
        AllowOtherDestinations.Visibility = mixedChain || canReadOtherDestinations ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.Content = files ? "Восстановить файлы" : "Извлечь файл базы";
        UpdateRestoreAvailability();
        Closed += (_, _) => { readingContents?.Cancel(); OverridePassword.Clear(); };
    }

    internal string? ParentDirectory { get; private set; }
    internal string? PasswordOverride { get; private set; }
    internal Func<OpenFolderDialog, bool>? PickFolderOverride { get; set; }
    internal Func<string?, bool, CancellationToken, Task<IReadOnlyList<FileRestoreItem>>>? ReadContents { get; set; }
    internal Action<RestoreSelectionWindow>? SelectionDialogOpened { get; set; }
    internal IReadOnlyCollection<string>? SelectedPaths { get; private set; }
    internal bool UseOtherDestinations => AllowOtherDestinations.Visibility == Visibility.Visible && AllowOtherDestinations.IsChecked == true;

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку назначения для восстановления" };
        if (!(PickFolderOverride?.Invoke(dialog) ?? (dialog.ShowDialog(this) == true))) return;
        ParentDirectory = TargetFolder.Text = dialog.FolderName;
        UpdateRestoreAvailability();
    }

    private void AlternatePassword_Expanded(object sender, RoutedEventArgs e) => OverridePassword.Focus();

    private void AlternatePassword_Collapsed(object sender, RoutedEventArgs e)
    {
        OverridePassword.Clear();
        PasswordError.Text = "";
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ParentDirectory is null) return;
        if (AlternatePassword.IsExpanded)
        {
            try { ArchivePasswords.Validate(OverridePassword.Password); }
            catch (ArgumentException error)
            {
                PasswordError.Text = error.Message;
                OverridePassword.Focus();
                return;
            }
            PasswordOverride = OverridePassword.Password;
        }
        DialogResult = true;
    }

    private async void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        if (ReadContents is null || readingContents is not null) return;
        using var cancellation = new CancellationTokenSource();
        readingContents = cancellation;
        ChooseFilesButton.IsEnabled = RestoreButton.IsEnabled = RestoreAllButton.IsEnabled = false;
        SelectionStatus.Text = "Загружается и проверяется индекс выбранной копии…";
        try
        {
            var items = await ReadContents(AlternatePassword.IsExpanded ? OverridePassword.Password : null, UseOtherDestinations, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            var dialog = new RestoreSelectionWindow(items, SelectedPaths) { Owner = this };
            SelectionDialogOpened?.Invoke(dialog);
            if (dialog.ShowDialog() == true) SelectedPaths = dialog.SelectedPaths;
            RefreshSelection();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested)
                SelectionStatus.Text = SelectionLabel() + "\nНе удалось прочитать содержимое: " + error.Message;
        }
        finally
        {
            readingContents = null;
            if (!cancellation.IsCancellationRequested)
            {
                RestoreAllButton.IsEnabled = true;
                UpdateRestoreAvailability();
            }
        }
    }

    private void RestoreAvailability_Changed(object sender, RoutedEventArgs e) => UpdateRestoreAvailability();

    private void UpdateRestoreAvailability()
    {
        var canRead = readingContents is null && (!mixedChain || UseOtherDestinations);
        ChooseFilesButton.IsEnabled = canRead;
        RestoreButton.IsEnabled = canRead && ParentDirectory is not null;
    }

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        SelectedPaths = null;
        RefreshSelection();
    }

    private string SelectionLabel() => SelectedPaths is { } paths
        ? $"Выбрано файлов и папок: {paths.Count}." : "Будет восстановлена вся копия.";

    private void RefreshSelection()
    {
        SelectionStatus.Text = SelectionLabel();
        RestoreAllButton.Visibility = SelectedPaths is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
