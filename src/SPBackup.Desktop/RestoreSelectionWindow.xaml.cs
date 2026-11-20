using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SPBackup.Core;

namespace SPBackup.Desktop;

public partial class RestoreSelectionWindow : Window
{
    private sealed class Entry(FileRestoreItem item, bool selected, Action<int> selectionChanged) : INotifyPropertyChanged
    {
        private bool isSelected = selected;
        public string Path => item.Path;
        public string Description => item.IsDirectory ? "Папка со всем содержимым" : item.FromEarlierSnapshot ? "Прежняя версия файла" : "Файл";
        public bool Selected
        {
            get => isSelected;
            set
            {
                if (value == isSelected) return;
                isSelected = value;
                selectionChanged(value ? 1 : -1);
                PropertyChanged?.Invoke(this, new(nameof(Selected)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly Entry[] entries;
    private readonly ICollectionView view;
    private int selectedCount;

    internal RestoreSelectionWindow(IReadOnlyList<FileRestoreItem> items, IReadOnlyCollection<string>? previous)
    {
        InitializeComponent();
        var selected = (previous ?? []).ToHashSet(StringComparer.Ordinal);
        entries = items.Select(item => new Entry(item, selected.Contains(item.Path), ChangeSelection)).ToArray();
        selectedCount = entries.Count(entry => entry.Selected);
        view = CollectionViewSource.GetDefaultView(entries);
        view.Filter = item => item is Entry entry && entry.Path.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        Entries.ItemsSource = view;
        ChangeSelection(0);
    }

    internal IReadOnlyCollection<string> SelectedPaths { get; private set; } = [];

    private void Search_Changed(object sender, TextChangedEventArgs e) => view?.Refresh();

    private void ChangeSelection(int delta)
    {
        selectedCount += delta;
        SelectionCount.Text = $"Выбрано: {selectedCount}";
        ApplyButton.IsEnabled = selectedCount > 0;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        SelectedPaths = entries.Where(entry => entry.Selected).Select(entry => entry.Path).ToArray();
        if (SelectedPaths.Count > 0) DialogResult = true;
    }
}
