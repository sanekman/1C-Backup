using System.IO;
using System.Windows;
using System.Windows.Controls;
using OneCBackup.Core;

namespace OneCBackup.Desktop;

public partial class MainWindow
{
    private bool changingOneCBasePicker;
    private int oneCListGeneration, oneCPlatformGeneration;
    private readonly OneCUserBase manualOneCBase = new("Своя база — указать папку вручную", "", "");
    private OneCSettings ReadOneCSettings(int timeout) => new()
    {
        AutoSelectPlatform = AutoOneCPlatform.IsChecked == true,
        PlatformVersion = OneCPlatformVersion.Text.Trim(),
        ExecutablePath = OneCExecutable.Text.Trim(), TimeoutMinutes = timeout
    };
    private void ResetOneCBasePicker()
    {
        changingOneCBasePicker = true;
        try { OneCBasePicker.ItemsSource ??= new[] { manualOneCBase }; OneCBasePicker.SelectedItem = manualOneCBase; }
        finally { changingOneCBasePicker = false; }
    }
    private async void RefreshOneCBases_Click(object sender, RoutedEventArgs e) => await RefreshOneCBasesAsync();
    private async Task RefreshOneCBasesAsync()
    {
        var generation = ++oneCListGeneration;
        var selected = OneCBasePicker.SelectedItem as OneCUserBase;
        OneCBaseListStatus.Text = "Чтение списка запуска 1С…";
        try
        {
            var list = await Task.Run(OneCUserBases.ReadCurrentUser);
            if (generation != oneCListGeneration) return;
            changingOneCBasePicker = true;
            try
            {
                OneCBasePicker.ItemsSource = new[] { manualOneCBase }.Concat(list.Bases).ToArray();
                OneCBasePicker.SelectedItem = list.Bases.FirstOrDefault(b => b.Directory == selected?.Directory) ?? manualOneCBase;
            }
            finally { changingOneCBasePicker = false; }
            OneCBaseListStatus.Text = list.Bases.Count == 0 ? "Файловые базы в списке пользователя не найдены. Укажите свою папку базы."
                : $"Найдено файловых баз: {list.Bases.Count}.";
            if (list.ServerBases > 0) OneCBaseListStatus.Text += " Серверные базы копируйте заданиями SQL Server или PostgreSQL.";
            if (list.UnreadableLists > 0) OneCBaseListStatus.Text += " Часть списков недоступна; можно указать базу вручную.";
        }
        catch (Exception ex)
        {
            if (generation != oneCListGeneration) return;
            DiagnosticLog.Current.Write(DiagnosticEvent.DesktopError, DiagnosticLevel.Warning, ex);
            OneCBaseListStatus.Text = "Не удалось прочитать список. Укажите свою папку базы вручную.";
        }
    }
    private void OneCSource_Changed(object sender, TextChangedEventArgs e)
    {
        if (changingOneCBasePicker || OneCPlatformVersion is null || OneCBasePicker?.SelectedItem is not OneCUserBase selected || selected.Directory.Length == 0) return;
        if (!Source.Text.Equals(selected.Directory, StringComparison.OrdinalIgnoreCase))
        { ResetOneCBasePicker(); OneCPlatformVersion.Clear(); }
    }
    private void OneCBase_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (changingOneCBasePicker || Source is null || OneCBasePicker.SelectedItem is not OneCUserBase selected) return;
        Source.Text = selected.Directory;
        OneCPlatformVersion.Text = selected.PlatformVersion;
        AutoOneCPlatform.IsChecked = true;
        if (selected.Directory.Length != 0 && string.IsNullOrWhiteSpace(JobName.Text)) JobName.Text = selected.Name;
        UpdateOneCPlatformControls();
    }
    private void AutoOneCPlatform_Changed(object sender, RoutedEventArgs e) => UpdateOneCPlatformControls();
    private void OneCPlatformVersion_Changed(object sender, TextChangedEventArgs e) => UpdateOneCPlatformControls();
    private void UpdateOneCPlatformControls()
    {
        if (AutoOneCPlatformPanel is null || ManualOneCPlatformPanel is null || OneCPlatformStatus is null || OneCExecutable is null) return;
        var auto = AutoOneCPlatform.IsChecked == true;
        AutoOneCPlatformPanel.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        ManualOneCPlatformPanel.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        _ = RefreshOneCPlatformAsync();
    }
    private async Task RefreshOneCPlatformAsync()
    {
        var generation = ++oneCPlatformGeneration;
        if (OneCPanel.Visibility != Visibility.Visible) return;
        var settings = ReadOneCSettings(120);
        OneCPlatformStatus.Text = "Поиск платформы…";
        try
        {
            var executable = await Task.Run(() => OneCPlatformLocator.Resolve(settings));
            if (generation != oneCPlatformGeneration) return;
            OneCPlatformStatus.Text = "Будет использована: " + executable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (generation == oneCPlatformGeneration) OneCPlatformStatus.Text = ex.Message;
        }
    }
}
