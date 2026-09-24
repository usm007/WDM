using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using WDM.Services;

namespace WDM;

/// <summary>Checklist for batch captures (1DM multi-post social dialog equivalent):
/// one row per captured link, checked rows become downloads.</summary>
public partial class BatchAddDialog : Window
{
    public sealed class BatchRow : INotifyPropertyChanged
    {
        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
        }
        public required string DisplayName { get; init; }
        public required string Url { get; init; }
        public required CaptureServer.BatchCaptureItem Source { get; init; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<BatchRow> _rows = new();

    public BatchAddDialog(List<CaptureServer.BatchCaptureItem> items)
    {
        InitializeComponent();
        foreach (var item in items)
        {
            _rows.Add(new BatchRow
            {
                DisplayName = string.IsNullOrWhiteSpace(item.FileName)
                    ? DownloadEngine.DeriveName(item.Url)
                    : item.FileName,
                Url = item.Url,
                Source = item,
            });
        }
        ItemsList.ItemsSource = _rows;
        RefreshTitle();
        Loaded += (_, _) => AddButton.Focus();
    }

    public List<CaptureServer.BatchCaptureItem> GetSelected() =>
        _rows.Where(r => r.IsChecked).Select(r => r.Source).ToList();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyTitleBar(this);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void ItemToggled(object sender, RoutedEventArgs e) => RefreshTitle();

    private void SelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsChecked = true;
        RefreshTitle();
    }

    private void SelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.IsChecked = false;
        RefreshTitle();
    }

    private void RefreshTitle()
    {
        int n = _rows.Count(r => r.IsChecked);
        TitleText.Text = $"Add downloads ({n} of {_rows.Count} selected)";
        AddButton.Content = n == 0 ? "Add selected" : $"Add {n} selected";
        AddButton.IsEnabled = n > 0;
    }

    private void AddClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
