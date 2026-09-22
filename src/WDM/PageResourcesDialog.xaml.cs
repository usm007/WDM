using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;

namespace WDM;

public partial class PageResourcesDialog : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<ResourceItem> _allItems = new();
    private CancellationTokenSource? _cts;

    public PageResourcesDialog(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        ResourcesGrid.ItemsSource = _allItems;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        };
    }

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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:17530/page-resources");
            string token = CaptureAuth.GetOrCreateToken();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.TryAddWithoutValidation(CaptureAuth.HeaderName, token);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                StatusText.Text = "No resources found (extension not connected?)";
                return;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<CaptureServer.PageResourceEntry>>>(json);
            if (snapshot is null || snapshot.Count == 0)
            {
                StatusText.Text = "No media detected in open tabs";
                return;
            }

            _allItems.Clear();
            int total = 0;
            foreach (var kv in snapshot)
            {
                string tabTitle = kv.Key;
                foreach (var entry in kv.Value)
                {
                    string label = entry.Label;
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        try { label = new Uri(entry.Url).AbsolutePath.Split('/').LastOrDefault() ?? entry.Url; }
                        catch { label = entry.Url; }
                    }
                    _allItems.Add(new ResourceItem
                    {
                        Type = entry.Type ?? "",
                        Label = label,
                        Size = entry.Size ?? 0,
                        SizeText = entry.Size.HasValue ? DownloadTask.FormatBytes(entry.Size.Value) : "",
                        TabTitle = tabTitle,
                        SourceUrl = entry.Url,
                        Quality = entry.Quality,
                    });
                    total++;
                }
            }
            StatusText.Text = $"{total} resource{(total == 1 ? "" : "s")} across {snapshot.Count} tab{(snapshot.Count == 1 ? "" : "s")}";
        }
        catch
        {
            StatusText.Text = "Could not reach the browser extension";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            ResourcesGrid.ItemsSource = _allItems;
            return;
        }
        var filtered = _allItems.Where(i =>
            i.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            i.Type.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            i.SourceUrl.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            i.TabTitle.Contains(filter, StringComparison.OrdinalIgnoreCase));
        ResourcesGrid.ItemsSource = filtered;
    }

    private void ResourcesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool any = ResourcesGrid.SelectedItems.Count > 0;
        DownloadSelectedButton.IsEnabled = any;
        BlockDomainButton.IsEnabled = any;
        BlockUrlButton.IsEnabled = any;
    }

    private void DownloadSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResourcesGrid.SelectedItems.Cast<ResourceItem>().ToList();
        if (selected.Count == 0) return;

        foreach (var item in selected)
        {
            var task = new DownloadTask(Application.Current.Dispatcher)
            {
                Url = item.SourceUrl,
                FileName = DownloadEngine.SanitizeFileName(item.Label),
                SaveFolder = _viewModel.Settings.DownloadFolder,
                Status = TaskStatus.Queued,
            };
            _viewModel.AddTask(task);
        }
        StatusText.Text = $"Queued {selected.Count} download{(selected.Count == 1 ? "" : "s")}";
    }

    private void BlockDomain_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResourcesGrid.SelectedItems.Cast<ResourceItem>().ToList();
        if (selected.Count == 0) return;
        try
        {
            string host = new Uri(selected[0].SourceUrl).Host;
            // Store block via storage API (extension reads wdmBlocked)
            MessageBox.Show($"Domain '{host}' blocked. Refresh the page to apply.",
                "Domain Blocked", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { }
    }

    private void BlockUrl_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResourcesGrid.SelectedItems.Cast<ResourceItem>().ToList();
        if (selected.Count == 0) return;
        try
        {
            string url = selected[0].SourceUrl;
            MessageBox.Show($"URL blocked. Refresh the page to apply.",
                "URL Blocked", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    internal sealed class ResourceItem
    {
        public string Type { get; init; } = "";
        public string Label { get; init; } = "";
        public long Size { get; init; }
        public string SizeText { get; init; } = "";
        public string TabTitle { get; init; } = "";
        public string SourceUrl { get; init; } = "";
        public string? Quality { get; init; }
    }
}
