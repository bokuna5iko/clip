using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Clip.Data;
using Clip.Models;
using Clip.Services;

namespace Clip.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ClipStore _store;
    private string _searchText = "";
    private bool _isSettings;
    private ClipItem? _selectedItem;

    public MainViewModel(ClipStore store)
    {
        _store = store;
        Items = new ObservableCollection<ClipItem>();
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        RefreshItems();
    }

    public ObservableCollection<ClipItem> Items { get; }
    public ICollectionView ItemsView { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ItemsView.Refresh();
        }
    }

    public bool IsSettings
    {
        get => _isSettings;
        set
        {
            if (_isSettings == value) return;
            _isSettings = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRecent));
        }
    }

    public bool IsRecent => !_isSettings;

    public ClipItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem == value) return;
            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    public bool StartupWithWindows
    {
        get => StartupService.IsEnabled();
        set => StartupService.SetEnabled(value);
    }

    public bool HasItems => Items.Count > 0;

    public void RefreshItems()
    {
        var list = _store.GetAll();

        Items.Clear();
        foreach (var item in list)
            Items.Add(item);

        OnPropertyChanged(nameof(HasItems));
        ItemsView.Refresh();
    }

    public void DeleteItem(ClipItem item)
    {
        _store.Delete(item.Id);
        RefreshItems();
    }

    public void ClearHistory()
    {
        _store.ClearAll();
        RefreshItems();
    }

    public static BitmapImage? LoadThumbnail(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 40;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private bool FilterItem(object obj)
    {
        if (obj is not ClipItem item)
            return false;
        if (string.IsNullOrWhiteSpace(_searchText))
            return true;

        var query = _searchText.Trim();
        return Contains(item.Preview, query)
               || Contains(item.TextContent, query)
               || Contains(item.FilesJson, query);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
